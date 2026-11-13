using System;
using System.Collections.Generic;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// The public API for other mods. Every member uses only plain types (string, byte[], int, bool, Type,
    /// GameObject, Action), so a mod can also call it through reflection without referencing this DLL:
    ///   var api = Type.GetType("SubnauticaMP.MPApi, SubnauticaMP");
    ///   api?.GetMethod("Send").Invoke(null, new object[] { "mymod.boom", bytes });
    /// Handlers registered with On stay registered across sessions. See docs/MOD_SUPPORT.md.
    /// </summary>
    public static class MPApi
    {
        static readonly Dictionary<string, Action<int, byte[]>> _handlers = new Dictionary<string, Action<int, byte[]>>();

        /// <summary>A multiplayer session runs and at least one other player is connected.</summary>
        public static bool IsMultiplayer { get { return SyncKit.Active; } }
        public static bool IsHost { get { return SyncKit.IsHost; } }
        /// <summary>This machine's player number (host = 0, -1 without a session).</summary>
        public static int LocalPeer { get { return SyncKit.LocalPeer; } }
        /// <summary>True while a change from another player is being applied. Do not send while it is true.</summary>
        public static bool IsApplyingRemote { get { return SyncKit.Applying; } }

        public static event Action SessionStarted;
        public static event Action SessionEnded;

        /// <summary>Send bytes to every other player. Prefix the name with your mod's id ("mymod.thing").</summary>
        public static void Send(string name, byte[] data)
        {
            if (!SyncKit.ShouldSend) return;
            SyncKit.Send("api." + name, w => w.Bytes(data ?? new byte[0]));
        }

        /// <summary>Send bytes to one player (0 = the host).</summary>
        public static void SendTo(int peer, string name, byte[] data)
        {
            if (!SyncKit.InSession) return;
            SyncKit.SendTo(peer, "api." + name, w => w.Bytes(data ?? new byte[0]));
        }

        /// <summary>Receive messages sent with Send/SendTo under this name. The handler gets (sender, bytes) and runs
        /// on the main thread inside ApplyRemote, so changes it makes are not sent back.</summary>
        public static void On(string name, Action<int, byte[]> handler)
        {
            _handlers[name] = handler;
            if (SyncKit.InSession) RegisterOne(name, handler);
        }

        /// <summary>Run a change that came from another player (sets IsApplyingRemote while it runs).</summary>
        public static void ApplyRemote(Action change) { SyncKit.Apply(change); }

        /// <summary>Keep these fields of a component type in step on every object of the shared world (by unique id).
        /// Call at startup (in your plugin's Awake), before anybody connects: both sides must register the same types.</summary>
        public static bool RegisterSyncedType(Type componentType, string[] fieldNames)
        {
            try { SyncRegistry.Register(componentType, fieldNames, componentType.Assembly.GetName().Name); return true; }
            catch (Exception e) { SyncKit.Warn("MPApi.RegisterSyncedType " + componentType + ": " + e.Message); return false; }
        }

        /// <summary>The game's unique id of an object (same on every machine for objects of the shared world).</summary>
        public static string IdOf(GameObject go) { return SyncKit.IdOf(go); }
        /// <summary>The object with that unique id on this machine, or null.</summary>
        public static GameObject Find(string id) { return SyncKit.Find(id); }

        // ------------------------------------------------------------------ plumbing

        static void RegisterOne(string name, Action<int, byte[]> handler)
        {
            SyncKit.On("api." + name, (from, r) =>
            {
                var data = r.Bytes() ?? new byte[0];
                SyncKit.Apply(() => handler(from, data));
            });
        }

        internal static void OnSessionStart()
        {
            foreach (var kv in _handlers) RegisterOne(kv.Key, kv.Value);
            try { if (SessionStarted != null) SessionStarted(); } catch (Exception e) { SyncKit.Warn("MPApi.SessionStarted handler: " + e); }
        }

        internal static void OnSessionEnd()
        {
            try { if (SessionEnded != null) SessionEnded(); } catch (Exception e) { SyncKit.Warn("MPApi.SessionEnded handler: " + e); }
        }
    }
}
