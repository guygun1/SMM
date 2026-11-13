using System;
using System.Collections.Generic;
using System.Linq;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Mod support, no code needed: any component type registered with the sync core is kept in step on every object
    /// of the shared world, matched by the game's unique id. A type is registered when
    ///  - a mod marks fields with [Synced] (MPCore.Sync.SyncedAttribute),
    ///  - a line "Full.Type.Name:field1,field2" is added to BepInEx/config/SubnauticaMP.synced.txt (any mod, no source needed),
    ///  - or a mod calls MPApi.RegisterSyncedType at startup.
    /// Every machine checks such components near its player twice a second and sends the fields that changed.
    /// The last change wins. Receivers write the fields inside SyncKit.Apply, so nothing echoes back.
    /// </summary>
    sealed class ModStateSync : ISyncFeature
    {
        public string Name { get { return "mod-state"; } }

        const float PollInterval = 0.5f, ScanInterval = 3f, Range = 80f;
        const float TypeCheckInterval = 2f;     // how often the registry is looked at for types worth watching
        const float TouchWindow = 4f;           // same as AutoCompat's interaction window
        const float SuppressAfterApply = 1f;    // after a remote value is applied, only rounding differences follow: not sent back
        const float StaleAfter = 300f;
        const int MaxLooked = 1200;

        sealed class Entry
        {
            public string Id, Key;
            public Component C;
            public SyncType St;
        }

        // "id|key" -> the bytes known to be in step, when they were last looked at, and until when not to send
        sealed class Known { public string Id; public byte[] Data; public float Seen, SuppressUntil; }

        // a received value whose component is not here yet: applied in Poll when it shows up
        sealed class Pending { public string TypeName; public byte[] Data; public float T; }

        readonly List<Entry> _entries = new List<Entry>();
        readonly Dictionary<string, Known> _known = new Dictionary<string, Known>();   // "id|key" -> last sent or received bytes
        readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        readonly List<string> _pruneKeys = new List<string>();
        static readonly List<Component> _compBuf = new List<Component>();               // reused for every object
        static readonly Dictionary<string, int> _counts = new Dictionary<string, int>(); // reused for every object
        float _nextPoll, _nextScan, _nextTypeCheck;
        bool _interesting;
        int _sent, _got;
        float _nextStats;

        public void Register()
        {
            SyncKit.On("mod.state", OnState);
        }

        static bool Interesting(SyncType st) { return st.Type != typeof(NetPlayerState); }

        public void Tick()
        {
            if (!SyncKit.Active || Player.main == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextTypeCheck)
            {
                _nextTypeCheck = now + TypeCheckInterval;
                _interesting = SyncRegistry.All().Any(Interesting);
            }
            if (!_interesting) return;
            if (now >= _nextScan) { _nextScan = now + ScanInterval; Scan(); }
            if (now >= _nextPoll) { _nextPoll = now + PollInterval; Poll(); }
            if (now >= _nextStats && (_sent + _got) > 0)
            {
                _nextStats = now + 30f;
                SyncKit.Info("mod-state: watching " + _entries.Count + " components, sent " + _sent + ", received " + _got);
            }
        }

        void Scan()
        {
            _entries.Clear();
            var me = Player.main.transform.position;
            float r2 = Range * Range;
            int looked = 0;
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (uid == null || string.IsNullOrEmpty(uid.Id)) continue;
                if ((uid.transform.position - me).sqrMagnitude > r2) continue;
                if (++looked > MaxLooked) break;     // bounded work per scan in a crowded base
                foreach (var e in EntriesOf(uid)) _entries.Add(e);
            }
            Prune(Time.unscaledTime);
        }

        /// <summary>Forget pending values and known values that are old or whose object no longer exists.</summary>
        void Prune(float now)
        {
            _pruneKeys.Clear();
            foreach (var kv in _pending) if (now - kv.Value.T > StaleAfter) _pruneKeys.Add(kv.Key);
            foreach (var k in _pruneKeys) _pending.Remove(k);
            _pruneKeys.Clear();
            foreach (var kv in _known)
                if (now - kv.Value.Seen > StaleAfter || !AliveId(kv.Value.Id)) _pruneKeys.Add(kv.Key);
            foreach (var k in _pruneKeys) _known.Remove(k);
            _pruneKeys.Clear();
        }

        static bool AliveId(string id)
        {
            UniqueIdentifier u;
            return UniqueIdentifier.TryGetIdentifier(id, out u) && u != null;
        }

        /// <summary>The registered components under one object, keyed by path + type + index (same on every machine).</summary>
        static IEnumerable<Entry> EntriesOf(UniqueIdentifier uid)
        {
            _counts.Clear();
            _compBuf.Clear();
            uid.GetComponentsInChildren<Component>(true, _compBuf);
            foreach (var c in _compBuf)
            {
                if (c == null) continue;
                SyncType st;
                if (!SyncRegistry.TryGet(c.GetType(), out st) || !Interesting(st)) continue;
                // a child with its own id is handled as its own object
                var owner = c.GetComponentInParent<UniqueIdentifier>();
                if (owner != uid) continue;
                string baseKey = PathOf(c.transform, uid.transform) + "#" + st.Name;
                int idx; _counts.TryGetValue(baseKey, out idx); _counts[baseKey] = idx + 1;
                yield return new Entry { Id = uid.Id, Key = baseKey + "#" + idx, C = c, St = st };
            }
        }

        static string PathOf(Transform t, Transform root)
        {
            if (t == root) return "";
            var p = t.name;
            for (var cur = t.parent; cur != null && cur != root; cur = cur.parent) p = cur.name + "/" + p;
            return p;
        }

        static byte[] Encode(Entry e)
        {
            var w = new ByteWriter(64);
            foreach (var f in e.St.Fields) f.Encode(w, e.C);
            return w.ToArray();
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// The host always sends. A joiner sends only for objects it touched lately (AutoCompat.Touched), so two machines
        /// never both send the same field from their own timers.
        /// </summary>
        static bool MaySend(string id, float now)
        {
            if (SyncKit.IsHost) return true;
            float t;
            return AutoCompat.Touched.TryGetValue(id, out t) && now - t < TouchWindow;
        }

        void Poll()
        {
            float now = Time.unscaledTime;
            foreach (var e in _entries)
            {
                if (e.C == null) continue;
                string k = e.Id + "|" + e.Key;
                Pending p;
                if (_pending.TryGetValue(k, out p))
                {
                    // a value that came before this component was here: apply it now
                    _pending.Remove(k);
                    if (p.TypeName == e.St.Name) ApplyTo(e, p.Data, now);
                    continue;
                }
                byte[] cur;
                try { cur = Encode(e); }
                catch { continue; }
                Known kn;
                if (!_known.TryGetValue(k, out kn))
                {
                    _known[k] = new Known { Id = e.Id, Data = cur, Seen = now };   // first look: this is the state from the shared save, nothing to send
                    continue;
                }
                kn.Seen = now;
                if (Same(kn.Data, cur)) continue;
                if (now < kn.SuppressUntil) { kn.Data = cur; continue; }   // a value was just applied here: only rounding differs, do not send it
                if (!MaySend(e.Id, now)) continue;                            // not ours to send: keep the reference as it is
                kn.Data = cur;
                var entry = e; var data = cur;
                SyncKit.Send("mod.state", w => { w.Str(entry.Id); w.Str(entry.Key); w.Str(entry.St.Name); w.Bytes(data); });
                _sent++;
            }
        }

        /// <summary>Writes a received value into the component. Only a value that was really applied becomes "known".</summary>
        bool ApplyTo(Entry e, byte[] data, float now)
        {
            bool ok = false;
            SyncKit.Apply(() =>
            {
                var rd = new ByteReader(data);
                foreach (var f in e.St.Fields) f.Decode(rd, e.C);
                ok = true;
            });
            if (!ok) return false;
            string k = e.Id + "|" + e.Key;
            Known kn;
            if (!_known.TryGetValue(k, out kn)) { kn = new Known { Id = e.Id }; _known[k] = kn; }
            kn.Data = data; kn.Seen = now; kn.SuppressUntil = now + SuppressAfterApply;
            return true;
        }

        void OnState(int from, ByteReader r)
        {
            string id = r.Str(), key = r.Str(), typeName = r.Str();
            var data = r.Bytes();
            if (id == null || key == null || data == null) return;
            _got++;
            float now = Time.unscaledTime;
            Entry e = null;
            var go = SyncKit.Find(id);   // null: not loaded here
            var uid = go != null ? go.GetComponent<UniqueIdentifier>() : null;
            if (uid != null) e = EntriesOf(uid).FirstOrDefault(x => x.Key == key);
            if (e == null || e.St.Name != typeName)
            {
                // not here (yet): kept, and applied in Poll when the component shows up
                _pending[id + "|" + key] = new Pending { TypeName = typeName, Data = data, T = now };
                return;
            }
            // nothing is sent from here: the host's relay already passes the change on to the other players
            ApplyTo(e, data, now);
        }
    }
}
