using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// One synced gameplay feature (doors, lockers, building...). Put each in its own file under Features/.
    /// Every class implementing this is found and started automatically when a multiplayer session starts.
    /// Harmony patches in the same file are applied automatically too (the plugin patches the whole assembly).
    /// </summary>
    public interface ISyncFeature
    {
        /// <summary>Short name for logs.</summary>
        string Name { get; }
        /// <summary>Called once per session. Register your messages here with SyncKit.On.</summary>
        void Register();
        /// <summary>Called every frame while a session is running (can be empty).</summary>
        void Tick();
    }

    /// <summary>
    /// The small API every sync feature uses. See docs/SYNC_API.md.
    ///
    /// The pattern:
    ///   1. A Harmony postfix on the game method that changes something (door opened, item added...).
    ///      In it: if (SyncKit.Active && !SyncKit.Applying) SyncKit.Send("door.open", w => { w.Str(SyncKit.IdOf(this)); w.Bool(open); });
    ///   2. In Register(): SyncKit.On("door.open", (from, r) => { var go = SyncKit.Find(r.Str()); ... SyncKit.Apply(() => door.Open()); });
    ///   SyncKit.Apply sets Applying while the remote change is replayed, so your own postfix does not send it back.
    /// </summary>
    public static class SyncKit
    {
        static SyncSession _s;
        static readonly List<ISyncFeature> _features = new List<ISyncFeature>();
        static int _applying;

        // ------------------------------------------------------------------ state

        /// <summary>A session is running and at least one other player is connected.</summary>
        public static bool Active { get { return _s != null && (_s.IsHost ? _s.PeerIds.Any() : _s.Ready); } }
        /// <summary>A session exists (maybe nobody connected yet).</summary>
        public static bool InSession { get { return _s != null; } }
        public static bool IsHost { get { return _s != null && _s.IsHost; } }
        /// <summary>This machine's peer id (host = 0).</summary>
        public static int LocalPeer { get { return _s != null ? _s.LocalPeer : -1; } }
        /// <summary>True while a change that came from another player is being applied. Do not send then.</summary>
        public static bool Applying { get { return _applying > 0; } }
        /// <summary>Send only if a session runs and this is not an echo of a remote change.</summary>
        public static bool ShouldSend { get { return Active && !Applying; } }

        // ------------------------------------------------------------------ messages

        /// <summary>Send to every other player. Build the payload in the callback.</summary>
        public static void Send(string name, Action<ByteWriter> write) { SendTo(SyncSession.All, name, write); }

        /// <summary>Send to the host only (target 0) or one peer.</summary>
        public static void SendTo(int peer, string name, Action<ByteWriter> write)
        {
            if (_s == null) return;
            var w = new ByteWriter();
            if (write != null) write(w);
            _s.SendMessage(name, w, peer);
        }

        /// <summary>Handle a message. The handler gets the sender's peer id and the payload reader.
        /// Exceptions are caught and logged with the message name.</summary>
        public static void On(string name, Action<int, ByteReader> handler)
        {
            if (_s == null) return;
            _s.RegisterMessage(name, (from, r) =>
            {
                try { handler(from, r); }
                catch (Exception e) { Warn("handler '" + name + "' from peer " + from + ": " + e); }
            });
        }

        /// <summary>Run a remote change. While it runs, Applying is true, so patches do not send it back.</summary>
        public static void Apply(Action a)
        {
            _applying++;
            try { a(); }
            catch (Exception e) { Warn("apply: " + e); }
            finally { _applying--; }
        }

        // ------------------------------------------------------------------ objects

        /// <summary>The game's unique id of the object (or of its nearest parent that has one). Same on every machine
        /// that loaded the same save. Null if none.</summary>
        public static string IdOf(Component c) { return c != null ? IdOf(c.gameObject) : null; }

        public static string IdOf(GameObject go)
        {
            if (go == null) return null;
            var uid = go.GetComponent<UniqueIdentifier>() ?? go.GetComponentInParent<UniqueIdentifier>();
            return uid != null && !string.IsNullOrEmpty(uid.Id) ? uid.Id : null;
        }

        /// <summary>Object with that unique id on this machine, or null (not loaded here, or never existed here).</summary>
        public static GameObject Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            UniqueIdentifier uid;
            return UniqueIdentifier.TryGetIdentifier(id, out uid) && uid != null ? uid.gameObject : null;
        }

        /// <summary>Find + GetComponent (also searches children). Null if missing.</summary>
        public static T Find<T>(string id) where T : Component
        {
            var go = Find(id);
            if (go == null) return null;
            return go.GetComponent<T>() ?? go.GetComponentInChildren<T>(true);
        }

        /// <summary>A path that identifies a child object under an object with an id, e.g. one door of a base piece.
        /// Returns (id, path). Use FindChild to resolve it on the other side.</summary>
        public static bool IdAndPath(Component c, out string id, out string path)
        {
            id = null; path = null;
            if (c == null) return false;
            var uid = c.GetComponentInParent<UniqueIdentifier>();
            if (uid == null || string.IsNullOrEmpty(uid.Id)) return false;
            id = uid.Id;
            var parts = new List<string>();
            for (var t = c.transform; t != null && t != uid.transform; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            path = string.Join("/", parts.ToArray());
            return true;
        }

        public static Transform FindChild(string id, string path)
        {
            var go = Find(id);
            if (go == null) return null;
            var t = go.transform;
            if (string.IsNullOrEmpty(path)) return t;
            foreach (var part in path.Split('/'))
            {
                int hash = part.LastIndexOf('#');
                string name = hash >= 0 ? part.Substring(0, hash) : part;
                int index; int.TryParse(hash >= 0 ? part.Substring(hash + 1) : "-1", out index);
                Transform next = null;
                if (index >= 0 && index < t.childCount && t.GetChild(index).name == name) next = t.GetChild(index);
                if (next == null) next = t.Find(name);
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        // ------------------------------------------------------------------ payload helpers

        public static void Vec(ByteWriter w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
        public static Vector3 Vec(ByteReader r) { return new Vector3(r.F32(), r.F32(), r.F32()); }
        public static void Rot(ByteWriter w, Quaternion q) { w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
        public static Quaternion Rot(ByteReader r) { return new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()); }
        /// <summary>TechTypes are sent by name: modded TechTypes get different numbers on different machines.</summary>
        public static void Tech(ByteWriter w, TechType t)
        {
            string name = null;
            try { name = TechTypeExtensions.AsString(t, false); } catch { }
            w.Str(name ?? ((int)t).ToString());
        }

        public static TechType Tech(ByteReader r)
        {
            var name = r.Str();
            if (string.IsNullOrEmpty(name)) return TechType.None;
            TechType t;
            try { if (TechTypeExtensions.FromString(name, out t, true)) return t; } catch { }
            int n;
            return int.TryParse(name, out n) ? (TechType)n : TechType.None;
        }

        // ------------------------------------------------------------------ logs

        public static void Info(string m) { if (Plugin.I != null) Plugin.I.Log.Info(m); }
        public static void Warn(string m) { if (Plugin.I != null) Plugin.I.Log.Warn(m); }

        // ------------------------------------------------------------------ lifecycle (called by the plugin)

        internal static void Attach(SyncSession s)
        {
            _s = s;
            _features.Clear();
            // features in this plugin and in any other mod that references it
            var types = new List<Type>();
            var me = typeof(SyncKit).Assembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm != me)
                {
                    bool refs = false;
                    try { refs = asm.GetReferencedAssemblies().Any(a => a.Name == me.GetName().Name); } catch { }
                    if (!refs) continue;
                }
                try { types.AddRange(asm.GetTypes()); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types.AddRange(e.Types.Where(x => x != null)); }
                catch { }
            }
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || !typeof(ISyncFeature).IsAssignableFrom(t)) continue;
                try
                {
                    var f = (ISyncFeature)Activator.CreateInstance(t);
                    f.Register();
                    _features.Add(f);
                }
                catch (Exception e) { Warn("feature " + t.Name + " failed to start: " + e); }
            }
            Info("sync features: " + string.Join(", ", _features.Select(f => f.Name).ToArray()));
            MPApi.OnSessionStart();
        }

        internal static void Detach()
        {
            if (_s != null) MPApi.OnSessionEnd();
            _s = null; _features.Clear(); _applying = 0;
        }

        internal static void Tick()
        {
            if (_s == null) return;
            foreach (var f in _features)
            {
                try { f.Tick(); }
                catch (Exception e) { Warn("feature " + f.Name + " tick: " + e.Message); }
            }
        }
    }
}
