using System;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Players are solid and can be hurt by each other. A remote player's body has a collider and a LiveMixin,
    /// so knives, tools and anything else that damages things finds it like a creature. The damage is not applied to
    /// the copy: it is sent to the player who owns that body, and applied to their real health there.
    /// </summary>
    static class PvP
    {
        /// <summary>Gives a remote player's root a body to bump into and a health component to hit.</summary>
        public static void MakeSolid(RemoteAvatar ra, Bounds worldBounds)
        {
            var go = ra.gameObject;
            var root = go.transform;
            float h = Mathf.Clamp(worldBounds.size.y, 1.2f, 2.2f);
            var centerLocal = root.InverseTransformPoint(worldBounds.center);

            var col = go.GetComponent<CapsuleCollider>() ?? go.AddComponent<CapsuleCollider>();
            col.direction = 1; col.height = h; col.radius = 0.35f; col.center = centerLocal;
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.isKinematic = true; rb.useGravity = false;
            go.layer = 0;
            if (go.GetComponent<SelectiveCollision>() == null) go.AddComponent<SelectiveCollision>().Own = col;

            if (go.GetComponent<LiveMixin>() == null)
            {
                // add it while inactive so its Awake sees data
                bool was = go.activeSelf;
                go.SetActive(false);
                var lm = go.AddComponent<LiveMixin>();
                var data = ScriptableObject.CreateInstance<LiveMixinData>();
                data.maxHealth = 100f; data.knifeable = true; data.destroyOnDeath = false; data.weldable = false;
                lm.data = data; lm.health = 100f;
                go.SetActive(was);
            }
        }

        /// <summary>Damage to a remote player's copy: forward it to the owner instead.</summary>
        public static bool InterceptDamage(LiveMixin lm, float amount, Vector3 pos, DamageType type)
        {
            if (lm == null) return true;
            var ra = lm.GetComponent<RemoteAvatar>();
            if (ra == null) return true;
            var s = Plugin.I != null ? Plugin.I.S : null;
            if (s != null && HostSettings.FriendlyFire && amount > 0f && !float.IsNaN(amount) && !float.IsInfinity(amount) && !SyncKit.Applying)
            {
                var w = new ByteWriter(24);
                w.F32(amount); w.VarI((int)type); w.F32(pos.x); w.F32(pos.y); w.F32(pos.z);
                s.SendMessage("dmg", w, ra.OwnerPeer);
                // one log line per second at most: a sustained attack hits every frame
                if (Time.unscaledTime - _lastLog > 1f)
                {
                    _lastLog = Time.unscaledTime;
                    Plugin.I.Log.Info("pvp: hit " + (ra.state != null ? ra.state.playerName : "?") + " for " + amount + " (" + type + ")");
                }
            }
            lm.health = 100f;   // the copy never dies; the owner's real health decides
            return false;
        }

        static float _lastLog, _lastLogIn;

        public static void OnDamage(int from, ByteReader r)
        {
            float amount = r.F32(); var type = (DamageType)(int)r.VarI();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var p = Player.main;
            if (p == null || p.liveMixin == null) return;
            if (!HostSettings.FriendlyFire || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            amount = Mathf.Min(amount, 500f);   // a malformed or hostile message must not be able to one-shot with a huge number
            // a hit that arrives while a remote change is being replayed must not be echoed to other peers
            SyncKit.Apply(() => p.liveMixin.TakeDamage(amount, pos, type, null));
            if (Plugin.I != null && Time.unscaledTime - _lastLogIn > 1f)
            {
                _lastLogIn = Time.unscaledTime;
                Plugin.I.Log.Info("pvp: peer " + from + " hit me for " + amount + " (" + type + ")");
            }
        }
    }

    /// <summary>
    /// A remote player's body only blocks players and creatures. Everything else with physics (the lifepod, dropped
    /// items, vehicles) ignores it, so a body moved by the network never pushes the world around.
    /// </summary>
    class SelectiveCollision : MonoBehaviour
    {
        public Collider Own;
        float _next;
        readonly System.Collections.Generic.HashSet<int> _done = new System.Collections.Generic.HashSet<int>();
        static readonly Collider[] _buf = new Collider[256];   // reused: no array allocation four times a second

        void Update()
        {
            if (Own == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (_done.Count > 20000) _done.Clear();
            int n = Physics.OverlapSphereNonAlloc(transform.position, 8f, _buf, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _buf[i];
                _buf[i] = null;
                if (c == null || c == Own || !_done.Add(c.GetInstanceID())) continue;
                if (Allowed(c)) continue;
                Physics.IgnoreCollision(Own, c, true);
            }
        }

        static bool Allowed(Collider c)
        {
            if (c is CharacterController) return true;                                   // the local player
            if (c.GetComponentInParent<Player>() != null) return true;
            if (c.GetComponentInParent<RemoteAvatar>() != null) return true;             // other remote players
            if (c.GetComponentInParent<Creature>() != null) return true;
            return c.attachedRigidbody == null;                                          // static world: harmless, keep it
        }
    }

    [HarmonyPatch(typeof(LiveMixin), "TakeDamage")]
    static class P_TakeDamage
    {
        static bool Prefix(LiveMixin __instance, float originalDamage, Vector3 position, DamageType type, ref bool __result)
        {
            try
            {
                if (!PvP.InterceptDamage(__instance, originalDamage, position, type)) { __result = false; return false; }
            }
            catch { }
            return true;
        }
    }
}
