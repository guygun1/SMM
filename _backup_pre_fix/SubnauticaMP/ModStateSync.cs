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

        sealed class Entry
        {
            public string Id, Key;
            public Component C;
            public SyncType St;
        }

        readonly List<Entry> _entries = new List<Entry>();
        readonly Dictionary<string, byte[]> _known = new Dictionary<string, byte[]>();   // "id|key" -> last sent or received bytes
        float _nextPoll, _nextScan;
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
            if (!SyncRegistry.All().Any(Interesting)) return;
            float now = Time.unscaledTime;
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
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (uid == null || string.IsNullOrEmpty(uid.Id)) continue;
                if ((uid.transform.position - me).sqrMagnitude > r2) continue;
                foreach (var e in EntriesOf(uid)) _entries.Add(e);
            }
        }

        /// <summary>The registered components under one object, keyed by path + type + index (same on every machine).</summary>
        static IEnumerable<Entry> EntriesOf(UniqueIdentifier uid)
        {
            var counts = new Dictionary<string, int>();
            foreach (var c in uid.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                SyncType st;
                if (!SyncRegistry.TryGet(c.GetType(), out st) || !Interesting(st)) continue;
                // a child with its own id is handled as its own object
                var owner = c.GetComponentInParent<UniqueIdentifier>();
                if (owner != uid) continue;
                string baseKey = PathOf(c.transform, uid.transform) + "#" + st.Name;
                int idx; counts.TryGetValue(baseKey, out idx); counts[baseKey] = idx + 1;
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

        void Poll()
        {
            foreach (var e in _entries)
            {
                if (e.C == null) continue;
                byte[] now;
                try { now = Encode(e); }
                catch { continue; }
                string k = e.Id + "|" + e.Key;
                byte[] last;
                if (_known.TryGetValue(k, out last))
                {
                    if (Same(last, now)) continue;
                }
                else
                {
                    _known[k] = now;   // first look: this is the state from the shared save, nothing to send
                    continue;
                }
                _known[k] = now;
                var entry = e; var data = now;
                SyncKit.Send("mod.state", w => { w.Str(entry.Id); w.Str(entry.Key); w.Str(entry.St.Name); w.Bytes(data); });
                _sent++;
            }
        }

        void OnState(int from, ByteReader r)
        {
            string id = r.Str(), key = r.Str(), typeName = r.Str();
            var data = r.Bytes();
            if (id == null || key == null || data == null) return;
            _got++;
            _known[id + "|" + key] = data;
            var go = SyncKit.Find(id);
            if (go == null) return;   // not loaded here
            var uid = go.GetComponent<UniqueIdentifier>();
            if (uid == null) return;
            var e = EntriesOf(uid).FirstOrDefault(x => x.Key == key);
            if (e == null || e.St.Name != typeName) return;
            SyncKit.Apply(() =>
            {
                var rd = new ByteReader(data);
                foreach (var f in e.St.Fields) f.Decode(rd, e.C);
            });
        }
    }
}
