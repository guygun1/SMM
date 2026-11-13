using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// The automatic compatibility layer: makes mods sync with no per-mod work.
    ///
    /// 1. Moving modded things. Modded submarines (any SubRoot that is not a base) and modded physics objects
    ///    (an object with a unique id, a non-kinematic Rigidbody and a component from a mod) are moved like vehicles
    ///    by EntitySync. Piloting a modded sub (Player mode Piloting with a non-base current sub) counts as driving it.
    /// 2. Modded component state. For components whose type comes from a mod (not the game, Unity or BepInEx), the
    ///    simple fields (bool, numbers, enums, strings, vectors, colours) on objects near players are compared about
    ///    once a second. The host sends what changed; a joiner sends only for objects it interacted with in the last
    ///    few seconds or is driving, so two machines never fight over a timer. Everyone writes received values.
    /// Config: Compat/AutoSyncModComponents (on by default), Compat/AutoSyncExclude (type names to leave alone).
    /// </summary>
    sealed class AutoCompat : ISyncFeature
    {
        public string Name { get { return "auto-compat"; } }

        const float PollInterval = 1f, ScanInterval = 4f, Range = 70f, InteractWindow = 4f;
        const int MaxComponents = 400;

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<string> _exclude;
        static bool _hooksAdded;

        // "objects this player touched recently" (root unique id -> time)
        internal static readonly Dictionary<string, float> Touched = new Dictionary<string, float>();

        sealed class FieldSet
        {
            public FieldInfo[] Fields;
        }

        sealed class Watched
        {
            public string Id, Key;
            public Component C;
            public FieldSet Fs;
            public byte[] Last;
        }

        static readonly Dictionary<Type, FieldSet> _fieldCache = new Dictionary<Type, FieldSet>();
        static readonly Dictionary<Assembly, bool> _isMod = new Dictionary<Assembly, bool>();

        // last value known to be in step on both sides ("id|key"); Seen is when it was last looked at
        sealed class Known { public string Id; public byte[] Data; public float Seen; }
        // a received value whose component was not near / not there yet: applied when it shows up
        sealed class Pend { public byte[] Data; public float T; }
        const float StaleAfter = 300f;

        readonly List<Watched> _watched = new List<Watched>();
        readonly Dictionary<string, Known> _known = new Dictionary<string, Known>();
        readonly Dictionary<string, Pend> _pending = new Dictionary<string, Pend>();
        readonly Dictionary<string, Watched> _prev = new Dictionary<string, Watched>();   // reused every scan
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();         // reused every object
        readonly List<string> _pruneKeys = new List<string>();
        static readonly List<MonoBehaviour> _mbBuf = new List<MonoBehaviour>();           // reused every object
        float _nextPoll, _nextScan, _nextStats;
        int _sent, _got;
        HashSet<string> _excluded = new HashSet<string>();

        public void Register()
        {
            if (Plugin.I != null && _enabled == null)
            {
                _enabled = Plugin.I.Config.Bind("Compat", "AutoSyncModComponents", true, "Automatically sync simple fields of components that come from other mods (host decides; joiners send what they interact with).");
                _exclude = Plugin.I.Config.Bind("Compat", "AutoSyncExclude", "", "Comma separated full type names of mod components that must not be auto-synced.");
            }
            _excluded = new HashSet<string>((_exclude != null ? _exclude.Value : "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            _watched.Clear(); _known.Clear(); _pending.Clear(); Touched.Clear();
            AddEntityHooks();
            SyncKit.On("auto.st", OnState);
        }

        // ------------------------------------------------------------------ 1. moving modded things

        static void AddEntityHooks()
        {
            if (_hooksAdded) return;
            _hooksAdded = true;
            try
            {
                var movable = typeof(EntitySync).GetField("ExtraMovable", BindingFlags.Public | BindingFlags.Static);
                var driven = typeof(EntitySync).GetField("ExtraDriven", BindingFlags.Public | BindingFlags.Static);
                if (movable != null) ((List<Func<GameObject, bool>>)movable.GetValue(null)).Add(IsModdedMovable);
                if (driven != null) ((List<Func<GameObject>>)driven.GetValue(null)).Add(PilotedModdedSub);
                SyncKit.Info("auto-compat: entity hooks " + (movable != null && driven != null ? "added" : "not available in this build"));
            }
            catch (Exception e) { SyncKit.Warn("auto-compat: entity hooks: " + e.Message); }
        }

        static bool IsModdedMovable(GameObject go)
        {
            var sr = go.GetComponent<SubRoot>();
            if (sr != null && !sr.isBase) return true;   // modded submarines
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) return false;
            if (go.GetComponent<Pickupable>() != null) return false;   // items: WorldSync
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c != null && IsModAssembly(c.GetType().Assembly)) return true;
            return false;
        }

        static GameObject PilotedModdedSub()
        {
            var p = Player.main;
            if (p == null) return null;
            if (p.currentSub != null && !p.currentSub.isBase && p.isPiloting) return p.currentSub.gameObject;
            return null;
        }

        // ------------------------------------------------------------------ 2. modded component state

        internal static bool IsModAssembly(Assembly a)
        {
            bool r;
            if (_isMod.TryGetValue(a, out r)) return r;
            var n = a.GetName().Name;
            r = !(n.StartsWith("Assembly-CSharp") || n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n.StartsWith("System")
                || n == "mscorlib" || n.StartsWith("Mono.") || n.StartsWith("BepInEx") || n == "0Harmony" || n.StartsWith("Harmony")
                || n == "SubnauticaMP" || n == "MPCore" || n.StartsWith("FMOD") || n.StartsWith("Newtonsoft") || n.StartsWith("Sentry")
                || n.StartsWith("LitJson") || n.StartsWith("com.rlabrecque") || n.StartsWith("UWE") || n.StartsWith("Platform")
                || n.StartsWith("Tobey") || n.StartsWith("Nautilus") || n.StartsWith("SMLHelper") || n.StartsWith("ECCLibrary"));
            _isMod[a] = r;
            return r;
        }

        static bool SimpleType(Type t)
        {
            return t == typeof(bool) || t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(string)
                || t == typeof(long) || t.IsEnum || t == typeof(Vector3) || t == typeof(Color) || t == typeof(Quaternion);
        }

        static FieldSet FieldsOf(Type t)
        {
            FieldSet fs;
            if (_fieldCache.TryGetValue(t, out fs)) return fs;
            var list = new List<FieldInfo>();
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && IsModAssembly(cur.Assembly); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsLiteral || !SimpleType(f.FieldType)) continue;
                    if (!f.IsPublic && !Attribute.IsDefined(f, typeof(SerializeField))) continue;   // what the mod itself saves/exposes
                    if (Attribute.IsDefined(f, typeof(NonSerializedAttribute))) continue;
                    if (f.Name.Contains("<")) continue;   // compiler generated
                    list.Add(f);
                }
            fs = new FieldSet { Fields = list.OrderBy(f => f.DeclaringType.FullName + "." + f.Name, StringComparer.Ordinal).ToArray() };
            _fieldCache[t] = fs;
            return fs;
        }

        static void WriteValue(ByteWriter w, object v)
        {
            switch (v)
            {
                case null: w.U8(0); break;
                case bool b: w.U8(1); w.Bool(b); break;
                case int i: w.U8(2); w.VarI(i); break;
                case float f: w.U8(3); w.F32((float)Math.Round(f, 3)); break;
                case double d: w.U8(4); w.F64(Math.Round(d, 3)); break;
                case string s: w.U8(5); w.Str(s); break;
                case long l: w.U8(6); w.VarI(l); break;
                case Vector3 v3: w.U8(7); w.F32((float)Math.Round(v3.x, 3)); w.F32((float)Math.Round(v3.y, 3)); w.F32((float)Math.Round(v3.z, 3)); break;
                case Color c: w.U8(8); w.F32(c.r); w.F32(c.g); w.F32(c.b); w.F32(c.a); break;
                case Quaternion q: w.U8(9); w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); break;
                default:
                    if (v.GetType().IsEnum) { w.U8(10); w.VarI(Convert.ToInt64(v)); }
                    else w.U8(0);
                    break;
            }
        }

        static object ReadValue(ByteReader r, Type target)
        {
            switch (r.U8())
            {
                case 1: return r.Bool();
                case 2: return (int)r.VarI();
                case 3: return r.F32();
                case 4: return r.F64();
                case 5: return r.Str();
                case 6: return r.VarI();
                case 7: return new Vector3(r.F32(), r.F32(), r.F32());
                case 8: return new Color(r.F32(), r.F32(), r.F32(), r.F32());
                case 9: return new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                case 10: { long n = r.VarI(); return target.IsEnum ? Enum.ToObject(target, n) : null; }
                default: return null;
            }
        }

        static byte[] Encode(Watched e)
        {
            var w = new ByteWriter(32);
            foreach (var f in e.Fs.Fields) WriteValue(w, f.GetValue(e.C));
            return w.ToArray();
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void Tick()
        {
            if (_enabled != null && !_enabled.Value) return;
            if (!SyncKit.Active || Player.main == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + ScanInterval; try { Scan(); } catch (Exception e) { SyncKit.Warn("auto-compat scan: " + e.Message); } }
            if (now >= _nextPoll) { _nextPoll = now + PollInterval; try { Poll(now); } catch (Exception e) { SyncKit.Warn("auto-compat poll: " + e.Message); } }
            if (now >= _nextStats && (_sent + _got) > 0) { _nextStats = now + 60f; SyncKit.Info("auto-compat: watching " + _watched.Count + " mod components, sent " + _sent + ", received " + _got); }
        }

        void Scan()
        {
            float now = Time.unscaledTime;
            _prev.Clear();
            foreach (var w in _watched) _prev[w.Id + "|" + w.Key] = w;
            _watched.Clear();
            var me = Player.main.transform.position;
            float r2 = Range * Range;
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (_watched.Count >= MaxComponents) break;
                if (uid == null || string.IsNullOrEmpty(uid.Id)) continue;
                if ((uid.transform.position - me).sqrMagnitude > r2) continue;
                _counts.Clear();
                uid.GetComponentsInChildren<MonoBehaviour>(true, _mbBuf);
                foreach (var c in _mbBuf)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (!IsModAssembly(t.Assembly) || _excluded.Contains(t.FullName)) continue;
                    SyncType st;
                    if (SyncRegistry.TryGet(t, out st)) continue;   // ModStateSync handles registered types
                    if (c.GetComponentInParent<UniqueIdentifier>() != uid) continue;
                    var fs = FieldsOf(t);
                    if (fs.Fields.Length == 0) continue;
                    string baseKey = Path(c.transform, uid.transform) + "#" + t.FullName;
                    int idx; _counts.TryGetValue(baseKey, out idx); _counts[baseKey] = idx + 1;
                    string key = baseKey + "#" + idx;
                    Watched old;
                    if (_prev.TryGetValue(uid.Id + "|" + key, out old) && old.C == c) { _watched.Add(old); continue; }
                    _watched.Add(new Watched { Id = uid.Id, Key = key, C = c, Fs = fs });
                }
            }
            // values that arrived before their component was here: apply them now that it is
            foreach (var e in _watched)
            {
                string k = e.Id + "|" + e.Key;
                Pend p;
                if (!_pending.TryGetValue(k, out p)) continue;
                _pending.Remove(k);
                ApplyTo(e, p.Data, now);
            }
            Prune(now);
        }

        /// <summary>Forget what is stale: pending values and known values that are old or whose object is gone, and touches.</summary>
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
            foreach (var kv in Touched) if (now - kv.Value > 60f) _pruneKeys.Add(kv.Key);
            foreach (var k in _pruneKeys) Touched.Remove(k);
            _pruneKeys.Clear();
        }

        static bool AliveId(string id)
        {
            UniqueIdentifier u;
            return UniqueIdentifier.TryGetIdentifier(id, out u) && u != null;
        }

        void SetKnown(string id, string key, byte[] data, float now)
        {
            string k = id + "|" + key;
            Known kn;
            if (!_known.TryGetValue(k, out kn)) { kn = new Known { Id = id }; _known[k] = kn; }
            kn.Data = data; kn.Seen = now;
        }

        /// <summary>Writes received values into the component. Only a value that was really applied becomes "known".</summary>
        bool ApplyTo(Watched e, byte[] data, float now)
        {
            bool ok = false;
            SyncKit.Apply(() =>
            {
                var rd = new ByteReader(data);
                foreach (var f in e.Fs.Fields)
                {
                    var v = ReadValue(rd, f.FieldType);
                    if (v == null && f.FieldType != typeof(string)) continue;
                    try { f.SetValue(e.C, v); } catch { }
                }
                ok = true;
            });
            if (ok) SetKnown(e.Id, e.Key, data, now);
            return ok;
        }

        void Poll(float now)
        {
            foreach (var e in _watched)
            {
                if (e.C == null) continue;
                byte[] cur;
                try { cur = Encode(e); } catch { continue; }
                string k = e.Id + "|" + e.Key;
                Known known;
                if (!_known.TryGetValue(k, out known)) { SetKnown(e.Id, e.Key, cur, now); continue; }   // first look: state from the shared save
                known.Seen = now;
                if (Same(known.Data, cur)) continue;
                if (!MaySend(e, now))
                {
                    // a joiner's own simulation drifted: keep the host's value as the reference, do not send
                    continue;
                }
                SetKnown(e.Id, e.Key, cur, now);
                var ee = e; var data = cur;
                SyncKit.Send("auto.st", w => { w.Str(ee.Id); w.Str(ee.Key); w.Bytes(data); });
                _sent++;
            }
        }

        void OnState(int from, ByteReader r)
        {
            string id = r.Str(), key = r.Str();
            var data = r.Bytes();
            if (id == null || key == null || data == null) return;
            _got++;
            float now = Time.unscaledTime;
            var e = _watched.FirstOrDefault(x => x.Id == id && x.Key == key);
            if (e == null || e.C == null)
            {
                // not near this player (or not there yet): kept, and applied in Scan when the component shows up
                _pending[id + "|" + key] = new Pend { Data = data, T = now };
                return;
            }
            // no forwarding here: the host's relay already passes a joiner's change to every other player (never back to the sender)
            ApplyTo(e, data, now);
        }

        static string Path(Transform t, Transform root)
        {
            if (t == root) return "";
            var p = t.name;
            for (var cur = t.parent; cur != null && cur != root; cur = cur.parent) p = cur.name + "/" + p;
            return p;
        }

        bool MaySend(Watched e, float now)
        {
            if (SyncKit.IsHost) return true;
            float t;
            if (Touched.TryGetValue(e.Id, out t) && now - t < InteractWindow) return true;
            var driven = PilotedModdedSub();
            return driven != null && SyncKit.IdOf(driven) == e.Id;
        }

    }

    /// <summary>Records what the local player clicks, so a joiner may send changes to those objects.</summary>
    [HarmonyPatch(typeof(GUIHand), "OnUpdate")]
    static class P_GuiHandTouch
    {
        static object _left, _right;
        static object[] _argLeft, _argRight;     // reused every frame: no argument array per call
        static System.Reflection.MethodInfo _down;
        static bool _broken;                     // setup failed once: do not retry (and throw) every frame

        static void Postfix(GUIHand __instance)
        {
            try
            {
                if (_broken || !SyncKit.Active) return;
                var target = __instance.GetActiveTarget();
                if (target == null) return;
                if (_down == null)
                {
                    var m = AccessTools.Method(typeof(GameInput), "GetButtonDown");
                    var bt = AccessTools.Inner(typeof(GameInput), "Button");
                    if (m == null || bt == null) { _broken = true; return; }
                    _left = Enum.Parse(bt, "LeftHand"); _right = Enum.Parse(bt, "RightHand");
                    _argLeft = new object[] { _left }; _argRight = new object[] { _right };
                    _down = m;
                }
                bool pressed = (bool)_down.Invoke(null, _argLeft) || (bool)_down.Invoke(null, _argRight);
                if (!pressed) return;
                var id = SyncKit.IdOf(target);
                if (id != null) AutoCompat.Touched[id] = Time.unscaledTime;
            }
            catch { if (_down == null) _broken = true; }
        }
    }
}
