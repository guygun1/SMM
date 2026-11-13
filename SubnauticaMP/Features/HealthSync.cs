using System;
using System.Collections.Generic;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Health, damage, healing and death of creatures, vehicles and Cyclops/bases (LiveMixin on an object that has a
    /// Creature, Vehicle or SubRoot).
    ///
    /// The host is the authority. The host sends the absolute health ("hp.set") whenever it changes. A joiner never
    /// sends an absolute value: it sends how much health it took or gave ("hp.hit", signed, summed over 0.2 s) to the
    /// host, which applies it to its own copy and broadcasts the result. Two players hitting one creature at the same
    /// moment therefore add up instead of the last message winning, and everybody ends on the host's number.
    /// Death: a health of 0 makes the receiver call LiveMixin.Kill, so corpses, drops and vehicle destruction run there too.
    /// </summary>
    sealed class HealthSync : ISyncFeature
    {
        const float MinInterval = 0.2f;

        sealed class Pending { public LiveMixin Lm; public float LastSent; public bool Dirty; public float LastValue = -1f; public float Delta; public DamageType Type; }
        static readonly Dictionary<string, Pending> _state = new Dictionary<string, Pending>();
        static readonly List<string> _tmp = new List<string>();

        public string Name { get { return "health"; } }

        public void Register()
        {
            _state.Clear();
            SyncKit.On("hp.set", (from, r) =>
            {
                string id = r.Str(); float hp = r.F32(); int type = (int)r.VarI();
                if (SyncKit.IsHost && from != 0) return;   // the host decides; it does not take absolute values from joiners
                if (float.IsNaN(hp) || float.IsInfinity(hp)) return;
                var lm = SyncKit.Find<LiveMixin>(id);
                if (lm == null || !Eligible(lm)) return;   // not loaded here: fine
                SyncKit.Apply(() => SetHealth(lm, hp, (DamageType)type));
                Pending p;
                if (_state.TryGetValue(id, out p)) { p.LastValue = hp; p.Dirty = false; p.Delta = 0f; }
            });

            // host only: a joiner reports damage (positive) or healing (negative) it caused
            SyncKit.On("hp.hit", (from, r) =>
            {
                string id = r.Str(); float delta = r.F32(); int type = (int)r.VarI();
                if (!SyncKit.IsHost || float.IsNaN(delta) || float.IsInfinity(delta) || Mathf.Abs(delta) < 0.001f) return;
                var lm = SyncKit.Find<LiveMixin>(id);
                if (lm == null || !Eligible(lm) || !lm.IsAlive()) return;
                float nh = lm.health - delta;
                var dt = (DamageType)type;
                if (nh <= 0f)
                {
                    lm.Kill(dt);                // the Kill patch broadcasts the death
                    return;
                }
                float before = lm.health;
                lm.health = Mathf.Min(nh, lm.maxHealth);
                AfterChange(lm, before);
                Changed(lm, lm.health + delta, true, dt);   // not through TakeDamage, so tell everybody ourselves
            });
        }

        public void Tick()
        {
            if (_state.Count == 0) return;
            if (!SyncKit.ShouldSend) { _state.Clear(); return; }
            float now = Time.unscaledTime;
            _tmp.Clear();
            foreach (var kv in _state)
            {
                var p = kv.Value;
                if (p.Lm == null) { _tmp.Add(kv.Key); continue; }
                if (p.Dirty && now - p.LastSent >= MinInterval) SendNow(kv.Key, p, p.Type);
            }
            foreach (var k in _tmp) _state.Remove(k);
        }

        // ------------------------------------------------------------------ helpers

        internal static bool Eligible(LiveMixin lm)
        {
            if (lm == null) return false;
            if (lm.player != null) return false;
            if (Player.main != null && lm == Player.main.liveMixin) return false;
            if (lm.GetComponent<RemoteAvatar>() != null || lm.GetComponentInParent<RemoteAvatar>() != null) return false;
            if (lm.GetComponent<Player>() != null) return false;
            if (lm.GetComponent<Creature>() != null || lm.GetComponent<Vehicle>() != null || lm.GetComponent<SubRoot>() != null) return true;
            var sub = lm.GetComponentInParent<SubRoot>();
            if (sub != null && sub.GetComponent<LiveMixin>() == lm) return true;
            var veh = lm.GetComponentInParent<Vehicle>();
            return veh != null && veh.GetComponent<LiveMixin>() == lm;
        }

        // Cyclops damage warnings (NotifyAllOfDamage) are replayed at most once per window per object. Without this,
        // every 0.2 s damage message while a fire burns made the crew warn again.
        const float NotifyWindow = 4f;
        static readonly Dictionary<int, float> _notifiedAt = new Dictionary<int, float>();

        /// <param name="before">health before this change. Only a real loss of health raises the damage warnings.</param>
        static void AfterChange(LiveMixin lm, float before)
        {
            if (lm.health >= before - 0.001f) return;        // healed, or the same value again: nothing to warn about
            try
            {
                var sub = lm.GetComponent<SubRoot>();
                if (sub == null) return;
                int key = lm.GetInstanceID();
                float last;
                if (_notifiedAt.TryGetValue(key, out last) && Time.unscaledTime - last < NotifyWindow) return;
                if (_notifiedAt.Count > 512) _notifiedAt.Clear();
                _notifiedAt[key] = Time.unscaledTime;
                var mgr = sub.GetComponentInChildren<CyclopsExternalDamageManager>(true);
                if (mgr != null) mgr.NotifyAllOfDamage();
            }
            catch { }
        }

        static void SetHealth(LiveMixin lm, float hp, DamageType type)
        {
            if (hp <= 0f)
            {
                if (lm.IsAlive()) lm.Kill(type);
                lm.health = 0f;
                return;
            }
            if (!lm.IsAlive())
            {
                // brought back (repair of a dead thing, or respawn): only possible when the game allows it.
                // Add only the missing part, and only when there is one, so a repeated message adds nothing and
                // does not run the repair effect again.
                if (lm.data != null && lm.data.canResurrect && hp > lm.health + 0.001f) lm.AddHealth(hp - lm.health);
                return;
            }
            float before = lm.health;
            float target = Mathf.Min(hp, lm.maxHealth);
            if (Mathf.Abs(target - before) < 0.001f) return;   // the same value again: replay nothing
            lm.health = target;
            AfterChange(lm, before);
        }

        static void SendNow(string id, Pending p, DamageType type)
        {
            p.Dirty = false; p.LastSent = Time.unscaledTime;
            if (SyncKit.IsHost)
            {
                float hp = p.Lm.health;
                if (Mathf.Abs(hp - p.LastValue) < 0.001f) return;
                p.LastValue = hp;
                SyncKit.Send("hp.set", w => { w.Str(id); w.F32(hp); w.VarI((int)type); });
            }
            else
            {
                float d = p.Delta; p.Delta = 0f;
                if (Mathf.Abs(d) < 0.001f) return;
                SyncKit.SendTo(0, "hp.hit", w => { w.Str(id); w.F32(d); w.VarI((int)type); });
            }
        }

        /// <param name="before">health before the change (a joiner reports the difference).</param>
        internal static void Changed(LiveMixin lm, float before, bool immediate, DamageType type)
        {
            try
            {
                if (!SyncKit.ShouldSend || !Eligible(lm)) return;
                string id = SyncKit.IdOf(lm);
                if (id == null) return;
                Pending p;
                if (!_state.TryGetValue(id, out p) || p.Lm != lm) { p = new Pending { Lm = lm }; _state[id] = p; }
                p.Type = type;
                if (!SyncKit.IsHost) p.Delta += before - lm.health;
                p.Dirty = true;
                if (immediate || Time.unscaledTime - p.LastSent >= MinInterval) SendNow(id, p, type);
            }
            catch (Exception e) { SyncKit.Warn("health: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(LiveMixin), "TakeDamage")]
    static class P_HealthDamage
    {
        static void Prefix(LiveMixin __instance, out float __state) { __state = __instance != null ? __instance.health : 0f; }

        static void Postfix(LiveMixin __instance, DamageType type, float __state)
        {
            try { HealthSync.Changed(__instance, __state, !__instance.IsAlive(), type); } catch { }
        }
    }

    [HarmonyPatch(typeof(LiveMixin), "AddHealth")]
    static class P_HealthHeal
    {
        static void Prefix(LiveMixin __instance, out float __state) { __state = __instance != null ? __instance.health : 0f; }

        static void Postfix(LiveMixin __instance, float __state)
        {
            try { HealthSync.Changed(__instance, __state, false, DamageType.Normal); } catch { }
        }
    }

    [HarmonyPatch(typeof(LiveMixin), "Kill")]
    static class P_HealthKill
    {
        static void Prefix(LiveMixin __instance, out float __state) { __state = __instance != null ? __instance.health : 0f; }

        static void Postfix(LiveMixin __instance, DamageType damageType, float __state)
        {
            try
            {
                if (__instance.health > 0f && !SyncKit.Applying) __instance.health = 0f;
                HealthSync.Changed(__instance, __state, true, damageType);
            }
            catch { }
        }
    }
}
