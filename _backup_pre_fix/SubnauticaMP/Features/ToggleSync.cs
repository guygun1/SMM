using System;
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
                SyncKit.Apply(() => { if (door.opened != open) door.SetState(open); });
            });

            SyncKit.On("tgl.light", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var lights = TglNet.Locate<ToggleLights>(id, path);
                if (lights == null) return;
                SyncKit.Apply(() => lights.SetLightsActive(on));
            });

            SyncKit.On("tgl.cyclops.lights", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool internalOn = r.Bool(), floodOn = r.Bool();
                var panel = TglNet.Locate<CyclopsLightingPanel>(id, path);
                if (panel == null) return;
                SyncKit.Apply(() =>
                {
                    if (panel.lightingOn != internalOn) panel.ToggleInternalLighting();
                    if (panel.floodlightsOn != floodOn) panel.ToggleFloodlights();
                });
            });

            SyncKit.On("tgl.cyclops.silent", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var button = TglNet.Locate<CyclopsSilentRunningAbilityButton>(id, path);
                if (button == null) return;
                SyncKit.Apply(() =>
                {
                    bool cur = Traverse.Create(button).Field("active").GetValue<bool>();
                    if (cur != on) Traverse.Create(button).Method(on ? "TurnOnSilentRunning" : "TurnOffSilentRunning").GetValue();
                });
            });

            SyncKit.On("tgl.cyclops.shield", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool on = r.Bool();
                var button = TglNet.Locate<CyclopsShieldButton>(id, path);
                if (button == null) return;
                SyncKit.Apply(() =>
                {
                    bool cur = Traverse.Create(button).Field("active").GetValue<bool>();
                    if (cur != on) Traverse.Create(button).Method(on ? "StartShield" : "StopShield").GetValue();
                });
            });
        }

        public void Tick() { }
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
        static void Postfix(CyclopsLightingPanel __instance)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                bool a = __instance.lightingOn, b = __instance.floodlightsOn;
                TglNet.Send("tgl.cyclops.lights", __instance, w => { w.Bool(a); w.Bool(b); });
            }
            catch (Exception e) { SyncKit.Warn("toggles: cyclops lights: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CyclopsLightingPanel), "ToggleFloodlights")]
    static class T_CyclopsFloodlights
    {
        static void Postfix(CyclopsLightingPanel __instance)
        {
            try
            {
                if (!SyncKit.ShouldSend) return;
                TglNet.Send("tgl.cyclops.lights", __instance, w => { w.Bool(__instance.lightingOn); w.Bool(__instance.floodlightsOn); });
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
