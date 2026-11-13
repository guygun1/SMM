using System;
using System.Collections.Generic;
using System.Reflection;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>Teaches the sync core the common Unity value types.</summary>
    public static class UnityCodecs
    {
        public static void Register()
        {
            Codecs.Register<Vector2>((w, v) => { w.F32(v.x); w.F32(v.y); }, r => new Vector2(r.F32(), r.F32()));
            Codecs.Register<Vector3>((w, v) => { w.F32(v.x); w.F32(v.y); w.F32(v.z); }, r => new Vector3(r.F32(), r.F32(), r.F32()));
            Codecs.Register<Quaternion>((w, v) => { w.F32(v.x); w.F32(v.y); w.F32(v.z); w.F32(v.w); }, r => new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()));
            Codecs.Register<Color>((w, v) => { w.F32(v.r); w.F32(v.g); w.F32(v.b); w.F32(v.a); }, r => new Color(r.F32(), r.F32(), r.F32(), r.F32()));
        }
    }

    /// <summary>
    /// Binds every registered component under a GameObject, so mods get syncing without writing any adapter code.
    /// The component key (path + type + index) is built from names only, so it is identical on every peer
    /// regardless of component order or load order.
    /// </summary>
    public static class AutoBind
    {
        public static int Bind(SyncSession s, NetObject o, GameObject root)
        {
            int n = 0;
            var counts = new Dictionary<string, int>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                SyncType st;
                if (!SyncRegistry.TryGet(c.GetType(), out st)) continue;
                string baseKey = PathOf(c.transform, root.transform) + "#" + st.Name;
                int idx; counts.TryGetValue(baseKey, out idx); counts[baseKey] = idx + 1;
                s.Bind(o, baseKey + "#" + idx, c);
                n++;
            }
            return n;
        }

        static string PathOf(Transform t, Transform root)
        {
            if (t == root) return "";
            var p = t.name;
            for (var cur = t.parent; cur != null && cur != root; cur = cur.parent) p = cur.name + "/" + p;
            return p;
        }
    }

    /// <summary>
    /// Finds every component type that has [Synced] members in any loaded assembly (so mods that reference MPCore
    /// are picked up with no setup), and registers third-party types listed in BepInEx/config/SubnauticaMP.synced.txt.
    /// </summary>
    public static class TypeDiscovery
    {
        public static void RegisterAttributed(ILog log)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n.StartsWith("System") || n.StartsWith("mscorlib") || n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n.StartsWith("Mono.")) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;
                    if (!HasSynced(t)) continue;
                    try { SyncRegistry.Register(t, null, n); log.Info("synced type: " + t.FullName + " (" + n + ")"); }
                    catch (Exception e) { log.Error("cannot sync " + t.FullName + ": " + e.Message); }
                }
            }
        }

        static bool HasSynced(Type t)
        {
            var f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMembers(f))
                if (Attribute.IsDefined(m, typeof(SyncedAttribute))) return true;
            return false;
        }

        /// <summary>Line format: Full.Type.Name:field1,field2   (lines starting with # are comments)</summary>
        public static void RegisterFromFile(string path, ILog log)
        {
            if (!System.IO.File.Exists(path)) return;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int colon = line.IndexOf(':');
                if (colon < 0) { log.Warn("bad line in synced list: " + line); continue; }
                string typeName = line.Substring(0, colon).Trim();
                var members = line.Substring(colon + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < members.Length; i++) members[i] = members[i].Trim();
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(typeName, false);
                    if (t != null) break;
                }
                if (t == null) { log.Warn("synced list: type not found " + typeName); continue; }
                try { SyncRegistry.Register(t, members, t.Assembly.GetName().Name); log.Info("synced type (config): " + typeName); }
                catch (Exception e) { log.Error("synced list " + typeName + ": " + e.Message); }
            }
        }
    }
}
