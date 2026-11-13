using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// "Unstuck": works in every state, also during an animation, a cinematic, a vehicle hatch, or a locked mode. Pressed from the
    /// options tab or with a key (default F7, config Controls/UnstuckKey). It does not wait for anything:
    ///  1. ends every player cinematic that is running (the hatch, bed, vehicle enter/exit animations),
    ///  2. clears the player's cinematic flag and leaves any locked mode (piloting, a seat),
    ///  3. turns the character controller back on and stops all movement,
    ///  4. moves the player to the nearest free standing spot: the last place the guard saw them clean if it is near, otherwise
    ///     above or around where they are, checked so the new place is not inside anything.
    /// Each step is separate, so a game call that does not exist in this version costs only that step. Every step is logged.
    /// </summary>
    static class Unstuck
    {
        static ConfigEntry<KeyCode> _key;

        internal static void Init(ConfigFile cfg)
        {
            if (cfg == null || _key != null) return;
            _key = cfg.Bind("Controls", "UnstuckKey", KeyCode.F7, "Press to get unstuck: ends any animation, frees the player and moves them to a free spot nearby. Works in any state.");
        }

        /// <summary>Called every frame from Plugin.Update, before anything else, so a stuck state cannot block it.</summary>
        internal static void Tick()
        {
            if (_key == null || Player.main == null) return;
            try { if (Input.GetKeyDown(_key.Value)) Run(); } catch { }
        }

        internal static void Run()
        {
            var p = Player.main;
            if (p == null) return;
            SyncKit.Info("unstuck: started at " + p.transform.position);

            // 1. every cinematic controller that is playing
            try
            {
                int ended = 0;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<PlayerCinematicController>())
                {
                    if (c == null) continue;
                    try
                    {
                        if (GetBool(c, "cinematicModeActive") == false) continue;      // known idle: leave it alone
                        Traverse.Create(c).Method("EndCinematicMode").GetValue();
                        ended++;
                    }
                    catch (Exception e) { SyncKit.Warn("unstuck: ending cinematic on " + c.name + ": " + e.Message); }
                }
                SyncKit.Info("unstuck: ended " + ended + " cinematic(s)");
            }
            catch (Exception e) { SyncKit.Warn("unstuck: cinematics: " + e.Message); }

            // 2. the player's own flags and mode
            try { SetBool(p, "cinematicModeActive", false); } catch { }
            try
            {
                var mode = Traverse.Create(p).Method("GetMode").GetValue();
                if (mode != null && mode.ToString() != "Normal")
                {
                    var m = AccessTools.GetDeclaredMethods(typeof(Player)).FirstOrDefault(x => x.Name == "ExitLockedMode");
                    if (m != null)
                    {
                        var args = m.GetParameters().Select(pr => pr.ParameterType.IsValueType ? Activator.CreateInstance(pr.ParameterType) : null).ToArray();
                        m.Invoke(p, args);
                        SyncKit.Info("unstuck: left mode " + mode);
                    }
                }
            }
            catch (Exception e) { SyncKit.Warn("unstuck: leaving locked mode: " + (e.InnerException != null ? e.InnerException.Message : e.Message)); }

            // 3. the controller
            var cc = p.GetComponent<CharacterController>();
            try
            {
                var pc = Traverse.Create(p).Field("playerController").GetValue();
                if (pc != null) Traverse.Create(pc).Method("SetEnabled", new object[] { true }).GetValue();
            }
            catch { }
            try { var rb = p.GetComponent<Rigidbody>(); if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } } catch { }

            // 4. a free place
            try
            {
                var spot = FindSpot(p, cc);
                if (cc != null) cc.enabled = false;
                p.transform.position = spot;
                if (cc != null) cc.enabled = true;
                try { p.SetPosition(spot); } catch { }
                SyncKit.Info("unstuck: moved to " + spot);
            }
            catch (Exception e) { SyncKit.Warn("unstuck: moving: " + e.Message); }
            try { ErrorMessage.AddMessage("Unstuck"); } catch { }
        }

        static Vector3 FindSpot(Player p, CharacterController cc)
        {
            var here = p.transform.position;
            var cand = new List<Vector3>();
            // The clean place is a world position. After a Cyclops carried the player 30 m, a place that was clean a minute ago is 30 m
            // behind in open water: only use one that is recent and close (this was the 30 m drop after Unstuck).
            if (PlayerGuard.HasGood && Time.unscaledTime - PlayerGuard.GoodTime < 5f && (PlayerGuard.GoodWorld - here).sqrMagnitude < 6f * 6f) cand.Add(PlayerGuard.GoodWorld);
            cand.Add(here + Vector3.up * 1.5f);
            foreach (float r in new[] { 1.5f, 3f, 5f })
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    var off = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    cand.Add(here + off); cand.Add(here + off + Vector3.up * 1.5f);
                }
            cand.Add(here + Vector3.up * 4f);
            bool was = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;                      // do not collide with ourselves
            try
            {
                float r2 = cc != null ? cc.radius : 0.4f, h = cc != null ? Mathf.Max(cc.height, r2 * 2f) : 1.7f;
                var cen = cc != null ? cc.center : new Vector3(0f, 0.9f, 0f);
                foreach (var c in cand)
                {
                    var mid = c + cen;
                    var a = mid + Vector3.up * (h * 0.5f - r2);
                    var b = mid - Vector3.up * (h * 0.5f - r2);
                    if (!Physics.CheckCapsule(a, b, r2 * 0.9f, ~0, QueryTriggerInteraction.Ignore)) return c;
                }
            }
            finally { if (cc != null) cc.enabled = was; }
            return here + Vector3.up * 4f;
        }

        internal static bool? GetBool(object o, string name)
        {
            var t = Traverse.Create(o);
            try { if (t.Property(name).PropertyExists()) return t.Property(name).GetValue<bool>(); } catch { }
            try { if (t.Field(name).FieldExists()) return t.Field(name).GetValue<bool>(); } catch { }
            return null;
        }

        static void SetBool(object o, string name, bool v)
        {
            var t = Traverse.Create(o);
            try { if (t.Property(name).PropertyExists()) { t.Property(name).SetValue(v); return; } } catch { }
            try { if (t.Field(name).FieldExists()) t.Field(name).SetValue(v); } catch { }
        }
    }
}
