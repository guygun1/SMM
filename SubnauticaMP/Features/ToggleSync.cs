using System;
using System.Collections.Generic;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Syncs simple on/off state: doors (Openable), base bulkhead doors, Seamoth/Prawn lights (ToggleLights),
    /// Cyclops lighting panel (internal lights, floodlights), Cyclops silent running and shields.
    /// Each change is sent from a Harmony postfix on the game's own method and replayed on the other side
    /// with the same unique id and child path. See docs/SYNC_API.md.
    /// </summary>
    sealed class ToggleSync : ISyncFeature
    {
        public string Name { get { return "toggles"; } }

        public void Register()
        {
            SyncKit.On("tgl.door", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool open = r.Bool();
                var door = TglNet.Locate<Openable>(id, path);
                if (door == null) return;
                if (!DoorGuard.ShouldApply(id + "/" + path, open)) return;   // the same state again: no second animation and sound
                SyncKit.Apply(() =>
                {
                    if (door.isOpen == open) return;
                    if (open) door.Open(); else door.Close();
                });
            });

            SyncKit.On("tgl.bulkhead", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool open = r.Bool();
                var door = TglNet.Locate<BulkheadDoor>(id, path);
                if (door == null) return;
                if (!DoorGuard.ShouldApply(id + "/" + path, open)) return;
                SyncKit.Apply(() => { if (door.opened != open) door.SetState(open); });
            });

            SyncKit.On("tgl.light", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var lights = TglNet.Locate<ToggleLights>(id, path);
                if (lights == null) return;
                // compare first: a repeated message must not click the lights again
                SyncKit.Apply(() => { if (lights.lightsActive != on) lights.SetLightsActive(on); });
            });

            SyncKit.On("tgl.cyclops.lights", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool internalOn = r.Bool(), floodOn = r.Bool();
                var panel = TglNet.Locate<CyclopsLightingPanel>(id, path);
                if (panel == null) return;
                CyclopsSync.Touch(panel, "lights");     // a remote user action: reconciliation waits before it overwrites this
                CyclopsSync.ReplayLights(panel, internalOn, floodOn);
            });

            SyncKit.On("tgl.cyclops.silent", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var button = TglNet.Locate<CyclopsSilentRunningAbilityButton>(id, path);
                if (button == null) return;
                CyclopsSync.Touch(button, "silent");
                CyclopsSync.ReplaySilent(button, on);
            });

            SyncKit.On("tgl.cyclops.shield", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var button = TglNet.Locate<CyclopsShieldButton>(id, path);
                if (button == null) return;
                CyclopsSync.Touch(button, "shield");
                CyclopsSync.ReplayShield(button, on);
            });
        }

        public void Tick() { }
    }

    /// <summary>
    /// Keeps door messages from echoing between machines. A door that was just set by a remote message reacts on its own
    /// (it closes by itself, or its trigger fires) and that reaction is not a new decision of this player, so:
    ///  - a change made right after a remote message for the same door is not sent back (QuietAfterApply);
    ///  - only a player who is near the door sends its changes (SendRange): the machine of a faraway player only
    ///    replays what it receives;
    ///  - the same door is not sent more than once in MinSendGap, and the same state is not applied twice in ApplyDedupe.
    /// Without this, a client standing at the Cyclops entrance and the host kept re-opening and re-closing the door.
    /// </summary>
    internal static class DoorGuard
    {
        const float QuietAfterApply = 1.0f, SendRange = 12f, MinSendGap = 0.4f, ApplyDedupe = 1.5f;
        const int DictMax = 2048;
        static readonly Dictionary<string, float> _quietUntil = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _sentAt = new Dictionary<string, float>();
        static readonly Dictionary<string, KeyValuePair<bool, float>> _applied = new Dictionary<string, KeyValuePair<bool, float>>();

        /// <summary>Remote message: true if it should be replayed here. Starts the quiet period for that door.</summary>
        internal static bool ShouldApply(string key, bool open)
        {
            float now = Time.unscaledTime;
            KeyValuePair<bool, float> last;
            if (_applied.TryGetValue(key, out last) && last.Key == open && now - last.Value < ApplyDedupe) { DebugLog.Log("door", "remote " + (open ? "open" : "close") + " of " + key + " dropped (repeat)", 10); return false; }
            if (_applied.Count > DictMax) { _applied.Clear(); _quietUntil.Clear(); _sentAt.Clear(); }
            _applied[key] = new KeyValuePair<bool, float>(open, now);
            _quietUntil[key] = now + QuietAfterApply;
            DebugLog.Log("door", "remote " + (open ? "open" : "close") + " of " + key + " applied", 10);
            return true;
        }

        /// <summary>Local change of a door: true if it may be sent.</summary>
        internal static bool ShouldSend(Component door, bool open)
        {
            if (door == null) return false;
            string id, path;
            if (!SyncKit.IdAndPath(door, out id, out path)) return false;
            string key = id + "/" + path;
            float now = Time.unscaledTime;
            float q;
            if (_quietUntil.TryGetValue(key, out q) && now < q) { DebugLog.Log("door", "local change of " + key + " not sent (reaction to a remote message)", 10); return false; }
            var p = Player.main;
            if (p != null && (p.transform.position - door.transform.position).sqrMagnitude > SendRange * SendRange) return false;   // not our door
            float at;
            if (_sentAt.TryGetValue(key, out at) && now - at < MinSendGap) return false;
            _sentAt[key] = now;
            return true;
        }
    }

    /// <summary>Shared helpers for the toggle patches and replays.</summary>
    internal static class TglNet
    {
        /// <summary>Find the component of type T at (id, path) on this machine, or null if not loaded here.</summary>
        internal static T Locate<T>(string id, string path) where T : Component
        {
            var t = SyncKit.FindChild(id, path);
            return t != null ? t.GetComponent<T>() : null;
        }

        /// <summary>Send msg with the object's id and path, followed by the extra payload written by 'extra'.</summary>
        internal static void Send(string msg, Component c, Action<ByteWriter> extra)
        {
            string id, path;
            if (c == null || !SyncKit.IdAndPath(c, out id, out path)) return;
            SyncKit.Send(msg, w =>
            {
                w.Str(id);
                w.Str(path);
                if (extra != null) extra(w);
            });
        }
    }

    // ---------------------------------------------------------------- senders (Harmony postfixes)

    // Each sender remembers the state before the game method ran and sends only a real change. Game code calls
    // these methods to restore saved state or on power loss, and that must not overwrite what the other player did.

    [HarmonyPatch(typeof(Openable), "PlayOpenAnimation")]
    static class T_Door
    {
        static void Prefix(Openable __instance, out bool __state) { __state = __instance != null && __instance.isOpen; }

        static void Postfix(Openable __instance, bool openState, bool __state)
        {
            try
            {
                if (!SyncKit.ShouldSend || __state == openState) return;
                if (!DoorGuard.ShouldSend(__instance, openState)) return;
                TglNet.Send("tgl.door", __instance, w => w.Bool(openState));
            }
            catch (Exception e) { SyncKit.Warn("toggles: door: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(BulkheadDoor), "SetState")]
    static class T_Bulkhead
    {
        static void Prefix(BulkheadDoor __instance, out bool __state) { __state = __instance != null && __instance.opened; }

        static void Postfix(BulkheadDoor __instance, bool open, bool __state)
        {
            try
            {
                if (!SyncKit.ShouldSend || __state == open) return;
                if (!DoorGuard.ShouldSend(__instance, open)) return;
                TglNet.Send("tgl.bulkhead", __instance, w => w.Bool(open));
            }
            catch (Exception e) { SyncKit.Warn("toggles: bulkhead: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(ToggleLights), "SetLightsActive")]
    static class T_Light
    {
        static void Prefix(ToggleLights __instance, out bool __state) { __state = __instance != null && __instance.lightsActive; }

        static void Postfix(ToggleLights __instance, bool isActive, bool __state)
        {
            try
            {
                if (!SyncKit.ShouldSend || __state == isActive) return;
                TglNet.Send("tgl.light", __instance, w => w.Bool(isActive));
            }
            catch (Exception e) { SyncKit.Warn("toggles: light: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsLightingPanel), "ToggleInternalLighting")]
    static class T_CyclopsInternalLights
    {
        static void Prefix(CyclopsLightingPanel __instance, out KeyValuePair<bool, bool> __state)
        {
            __state = __instance != null ? new KeyValuePair<bool, bool>(__instance.lightingOn, __instance.floodlightsOn) : default(KeyValuePair<bool, bool>);
        }

        static void Postfix(CyclopsLightingPanel __instance, KeyValuePair<bool, bool> __state)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                bool a = __instance.lightingOn, b = __instance.floodlightsOn;
                if (a == __state.Key && b == __state.Value) return;
                CyclopsSync.Touch(__instance, "lights");      // our own action: reconciliation waits before it overwrites this
                TglNet.Send("tgl.cyclops.lights", __instance, w => { w.Bool(a); w.Bool(b); });
            }
            catch (Exception e) { SyncKit.Warn("toggles: cyclops lights: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsLightingPanel), "ToggleFloodlights")]
    static class T_CyclopsFloodlights
    {
        static void Prefix(CyclopsLightingPanel __instance, out KeyValuePair<bool, bool> __state)
        {
            __state = __instance != null ? new KeyValuePair<bool, bool>(__instance.lightingOn, __instance.floodlightsOn) : default(KeyValuePair<bool, bool>);
        }

        static void Postfix(CyclopsLightingPanel __instance, KeyValuePair<bool, bool> __state)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                bool a = __instance.lightingOn, b = __instance.floodlightsOn;
                if (a == __state.Key && b == __state.Value) return;
                CyclopsSync.Touch(__instance, "lights");
                TglNet.Send("tgl.cyclops.lights", __instance, w => { w.Bool(a); w.Bool(b); });
            }
            catch (Exception e) { SyncKit.Warn("toggles: cyclops floodlights: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsSilentRunningAbilityButton), "TurnOnSilentRunning")]
    static class T_SilentOn
    {
        static void Prefix(CyclopsSilentRunningAbilityButton __instance, out bool __state) { __state = Active(__instance); }
        static void Postfix(CyclopsSilentRunningAbilityButton __instance, bool __state) { SendSilent(__instance, __state); }

        internal static bool Active(CyclopsSilentRunningAbilityButton b)
        {
            try { return b != null && Traverse.Create(b).Field("active").GetValue<bool>(); } catch { return false; }
        }

        internal static void SendSilent(CyclopsSilentRunningAbilityButton b, bool was)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                bool on = Active(b);
                if (on == was) return;
                CyclopsSync.Touch(b, "silent");
                TglNet.Send("tgl.cyclops.silent", b, w => w.Bool(on));
            }
            catch (Exception e) { SyncKit.Warn("toggles: silent running: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsSilentRunningAbilityButton), "TurnOffSilentRunning")]
    static class T_SilentOff
    {
        static void Prefix(CyclopsSilentRunningAbilityButton __instance, out bool __state) { __state = T_SilentOn.Active(__instance); }
        static void Postfix(CyclopsSilentRunningAbilityButton __instance, bool __state) { T_SilentOn.SendSilent(__instance, __state); }
    }

    [HarmonyPatch(typeof(CyclopsShieldButton), "StartShield")]
    static class T_ShieldOn
    {
        static void Prefix(CyclopsShieldButton __instance, out bool __state) { __state = Active(__instance); }
        static void Postfix(CyclopsShieldButton __instance, bool __state) { SendShield(__instance, __state); }

        internal static bool Active(CyclopsShieldButton b)
        {
            try { return b != null && Traverse.Create(b).Field("active").GetValue<bool>(); } catch { return false; }
        }

        internal static void SendShield(CyclopsShieldButton b, bool was)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                bool on = Active(b);
                if (on == was) return;
                CyclopsSync.Touch(b, "shield");
                TglNet.Send("tgl.cyclops.shield", b, w => w.Bool(on));
            }
            catch (Exception e) { SyncKit.Warn("toggles: shield: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsShieldButton), "StopShield")]
    static class T_ShieldOff
    {
        static void Prefix(CyclopsShieldButton __instance, out bool __state) { __state = T_ShieldOn.Active(__instance); }
        static void Postfix(CyclopsShieldButton __instance, bool __state) { T_ShieldOn.SendShield(__instance, __state); }
    }
}
