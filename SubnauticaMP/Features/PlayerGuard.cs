using System;
using System.Collections.Generic;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Keeps the local player's own state valid while things around them are moved by the network:
    ///  - a player in the air above a Cyclops that someone else drives is carried with it (otherwise a jump leaves them behind);
    ///  - a player whose body sticks into a network-moved vehicle or Cyclops is put back where they were last clean;
    ///  - a position that is not a number is replaced by the last good one.
    /// Runs every frame, with cheap early-outs. Every correction is logged.
    /// </summary>
    sealed class PlayerGuard : ISyncFeature
    {
        public string Name { get { return "player guard"; } }

        const float ShrinkRadius = 0.7f;     // a controller keeps its skin width away from colliders: a deeper overlap is a real intrusion
        const int MaxHits = 24;

        CharacterController _cc; bool _ccLooked;
        readonly Collider[] _hits = new Collider[MaxHits];
        readonly Dictionary<Transform, Vector3> _goodLocal = new Dictionary<Transform, Vector3>();   // owner -> last clean position in its own space
        readonly Dictionary<Transform, float> _goodTime = new Dictionary<Transform, float>();
        readonly Dictionary<Collider, Transform> _ownerCache = new Dictionary<Collider, Transform>();
        float _nextCacheClear, _nextLog;
        Vector3 _goodWorld; bool _hasGood;
        internal static Vector3 GoodWorld; internal static bool HasGood; internal static float GoodTime;   // for Unstuck: the last place the player was seen clean, and when
        int _bad; Transform _badOwner;
        float _graceUntil, _idleUntil, _lastPush; int _pushes;

        // carry in the air
        Transform _carrySub; Vector3 _carryPos; Quaternion _carryRot; bool _carryHas;

        /// <summary>Unscaled time until which a player cinematic (hatch, vehicle enter/exit, bed) is running or has only just ended.</summary>
        internal static float CinematicUntil;
        const float HoldRange = 20f;                 // a followed Cyclops closer than this to a player outside it keeps its hatch animators to itself
        static Type _hatchType; static bool _hatchLooked;

        /// <summary>
        /// True while the animators of a network-followed object must not be overwritten with the sender's values.
        /// The follower copies the owner's animator parameters onto this machine every frame. For a Cyclops hatch that fights the local
        /// player's own entry: the player opens the hatch here, the owner's value (closed) is written back the next frame, and the entry
        /// cinematic never reaches its end ("stuck in the tube" until the owner enters or leaves and the values happen to match again).
        /// So while a cinematic runs (plus a short tail), and for a Cyclops that a player outside it is next to, the values are not applied.
        /// </summary>
        internal static bool HoldAnims(SubRoot sr, Transform t)
        {
            var p = Player.main;
            if (p == null) return false;
            if (t == null) return false;
            if (Time.unscaledTime < CinematicUntil && (p.transform.position - t.position).sqrMagnitude < 30f * 30f) return true;
            if (sr == null) return false;
            // The entry tube counts as "inside" for the game (currentSub is already this Cyclops), yet the player is still working the
            // hatches there: next to an entry hatch the sender's values are held back whether the player is in or out.
            if (NearHatch(t, p.transform.position, 10f)) return true;
            if (p.currentSub != null) return false;
            return (p.transform.position - t.position).sqrMagnitude < HoldRange * HoldRange;
        }

        // entry hatches of a Cyclops, looked up once and kept for 10 s
        static readonly Dictionary<Transform, KeyValuePair<float, Component[]>> HatchCache = new Dictionary<Transform, KeyValuePair<float, Component[]>>();

        /// <summary>True when pos is within r metres of an entry hatch of this Cyclops.</summary>
        internal static bool NearHatch(Transform owner, Vector3 pos, float r)
        {
            if (!_hatchLooked) { _hatchLooked = true; try { _hatchType = HarmonyLib.AccessTools.TypeByName("CyclopsEntryHatch"); } catch { } }
            if (_hatchType == null || owner == null) return false;
            float now = Time.unscaledTime;
            KeyValuePair<float, Component[]> e;
            if (!HatchCache.TryGetValue(owner, out e) || now > e.Key)
            {
                Component[] hs;
                try { hs = owner.GetComponentsInChildren(_hatchType, true); } catch { hs = new Component[0]; }
                e = new KeyValuePair<float, Component[]>(now + 10f, hs);
                if (HatchCache.Count > 64) HatchCache.Clear();
                HatchCache[owner] = e;
            }
            var arr = e.Value;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && (arr[i].transform.position - pos).sqrMagnitude < r * r) return true;
            return false;
        }

        /// <summary>True when the player is swimming: head under the water line, and not inside a Cyclops or base.</summary>
        internal static bool InWater(Player p)
        {
            try { return p != null && p.currentSub == null && p.IsUnderwater(); } catch { return false; }
        }

        // network-followed Cyclops and vehicles near the player and how fast they move (looked up once a second)
        readonly List<Transform> _subs = new List<Transform>();
        readonly Dictionary<Transform, Vector3> _subLast = new Dictionary<Transform, Vector3>();
        float _nextSubScan, _lastSubTime, _fastUntil;
        bool _nearSub;

        /// <summary>Finds followed Cyclops or vehicles within 40 m and notes whether any of them is moving fast enough to run into a swimmer.</summary>
        void TrackSubs(Player p, float now)
        {
            if (now >= _nextSubScan)
            {
                _nextSubScan = now + 1f;
                _subs.Clear();
                try
                {
                    foreach (var f in UnityEngine.Object.FindObjectsOfType<EntityFollower>())
                    {
                        if (f == null || f.Dead) continue;
                        var sr = f.GetComponent<SubRoot>();
                        if (sr != null ? sr.isBase : f.GetComponent<Vehicle>() == null) continue;   // Cyclops and vehicles only
                        if ((f.transform.position - p.transform.position).sqrMagnitude < 40f * 40f) _subs.Add(f.transform);
                    }
                }
                catch { }
                _nearSub = _subs.Count > 0;
            }
            if (!_nearSub) return;
            float dt = now - _lastSubTime;
            if (dt < 0.1f) return;
            _lastSubTime = now;
            for (int i = 0; i < _subs.Count; i++)
            {
                var t = _subs[i]; if (t == null) continue;
                Vector3 last;
                if (_subLast.TryGetValue(t, out last) && dt < 1f && (t.position - last).magnitude / dt > 0.5f) _fastUntil = now + 1.5f;
                _subLast[t] = t.position;
            }
            if (_subLast.Count > 32) _subLast.Clear();
        }

        public void Register() { Reset(); HatchCache.Clear(); }

        void Reset()
        {
            _cc = null; _ccLooked = false; _goodLocal.Clear(); _goodTime.Clear(); _ownerCache.Clear();
            _graceUntil = _idleUntil = _lastPush = 0f; _pushes = 0;
            _hasGood = false; HasGood = false; _bad = 0; _badOwner = null; _carrySub = null; _carryHas = false;
            SoundWatch.ResetLocks();
        }

        public void Tick()
        {
            var p = Player.main;
            if (p == null || !SyncKit.InSession) { if (_hasGood || _carryHas) Reset(); return; }
            if (!_ccLooked) { _ccLooked = true; _cc = p.GetComponent<CharacterController>(); if (_cc == null) SyncKit.Warn("player guard: no CharacterController on the player; guard idle"); }
            if (_cc == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextCacheClear) { _nextCacheClear = now + 10f; _ownerCache.Clear(); PruneGood(now); }
            SoundWatch.Poll(now);        // once-played hatch / bay sounds are released when nobody is near any more

            // an animation owns the player (a hatch, a vehicle exit, a bed): the guard stays out of it, and a little after it
            if (Unstuck.GetBool(p, "cinematicModeActive") == true) { _graceUntil = now + 2f; CinematicUntil = now + 3f; _bad = 0; return; }
            if (now < _graceUntil || now < _idleUntil) { _bad = 0; return; }

            var pos = p.transform.position;
            if (!IsFinite(pos))
            {
                if (_hasGood) { Place(_goodWorld); Note("player position was not a number: put back at the last good place"); }
                return;
            }

            CarryInAir(p, pos);
            pos = p.transform.position;

            TrackSubs(p, now);
            // Nothing to correct away from a network-moved Cyclops: whatever overlaps the player there is the world, not a followed
            // copy, and a wrong detection must not teleport anyone.
            if (!_nearSub) { _bad = 0; _badOwner = null; _goodWorld = pos; _hasGood = true; GoodWorld = pos; HasGood = true; GoodTime = now; return; }
            bool swimming = InWater(p);
            // a Cyclops that moves fast pushes into a swimmer's body: look deeper (lower margin) so the overlap is seen early
            bool fast = now < _fastUntil;

            Transform owner;
            bool inside = Intrudes(pos, out owner, fast && swimming ? 1f : ShrinkRadius);
            if (!inside)
            {
                _bad = 0; _badOwner = null;
                _goodWorld = pos; _hasGood = true; GoodWorld = pos; HasGood = true; GoodTime = now;
                RememberClean(pos);
                return;
            }
            // one frame of overlap can be a hand-over between physics steps: act on the second (at once for a swimmer next to a fast Cyclops,
            // whose hull moves several metres a second and would otherwise be passed through before the second look)
            if (!(fast && swimming) && ++_bad < 2) { _badOwner = owner; return; }
            _bad = 0;
            // pushed again and again in a row: something here is not what the guard thinks it is. Stop for a while rather than trap the player.
            if (now - _lastPush < 1.5f) _pushes++; else _pushes = 1;
            _lastPush = now;
            if (_pushes >= 3) { _idleUntil = now + 10f; _pushes = 0; Note("moved out of '" + (owner != null ? owner.name : "?") + "' three times in a row: guard off for 10 s"); return; }
            Vector3 target;
            float t;
            if (owner != null && _goodLocal.TryGetValue(owner, out target) && _goodTime.TryGetValue(owner, out t) && now - t < 3f)
            {
                Place(owner.TransformPoint(target));
                Note("player was inside '" + owner.name + "': put back where they were last clear of it");
            }
            else if (owner != null)
            {
                var away = pos - owner.position; away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                Place(pos + away.normalized * 1.2f);
                Note("player was inside '" + owner.name + "', no clean place known: pushed out sideways");
            }
        }

        // ------------------------------------------------------------------ in the air above a network-moved Cyclops

        void CarryInAir(Player p, Vector3 pos)
        {
            var sub = p.currentSub;
            Transform st = sub != null ? sub.transform : null;
            bool followed = st != null && IsNetworkMoved(st);
            if (!followed) { _carryHas = false; _carrySub = null; return; }

            if (_carryHas && _carrySub == st && !_cc.isGrounded)
            {
                // the player's place in the sub's space before this frame, put back through the sub's pose now
                var local = Quaternion.Inverse(_carryRot) * (pos - _carryPos);
                var want = st.position + st.rotation * local;
                var d = want - pos;
                // Moved with the controller's own Move, never by switching the controller off and on: that exits and re-enters every
                // trigger around the player each frame (hatches restarting their animation and sound, helm menus, sound zones).
                if (d.sqrMagnitude > 0.0000001f && d.sqrMagnitude < 25f && IsFinite(want))
                {
                    try { _cc.Move(d); } catch (Exception e) { SyncKit.Warn("player guard: could not carry the player: " + e.Message); }
                }
            }
            _carrySub = st; _carryPos = st.position; _carryRot = st.rotation; _carryHas = true;
        }

        static bool IsNetworkMoved(Transform t)
        {
            var f = t.GetComponent<EntityFollower>();
            return f != null && !f.Dead;
        }

        // ------------------------------------------------------------------ inside a vehicle or Cyclops

        bool Intrudes(Vector3 pos, out Transform owner, float shrink)
        {
            owner = null;
            float r = _cc.radius * shrink;
            var c = _cc.transform.TransformPoint(_cc.center);
            float half = Mathf.Max(_cc.height * 0.5f - 0.12f, r);
            var a = c + Vector3.up * (half - r);
            var b = c - Vector3.up * (half - r);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, r, _hits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i];
                if (col == null || col.isTrigger || col.transform.IsChildOf(_cc.transform)) continue;
                var o = OwnerOf(col);
                if (o == null) continue;
                // the Cyclops or vehicle the player is inside is theirs to stand in: its own walls and floor are not an intrusion
                var inSub = Player.main != null ? Player.main.currentSub : null;
                if (inSub != null && (o == inSub.transform || o.IsChildOf(inSub.transform) || inSub.transform.IsChildOf(o))) continue;
                // Walking into the entry tube of a Cyclops: the player is still "outside" until the hatch cinematic puts them in, and the
                // tube and hull colliders overlap them. The log showed the guard putting the joiner back on every try to enter.
                if (NearEntryHatch(o, pos)) continue;
                owner = o;
                return true;
            }
            return false;
        }

        /// <summary>True when pos is within 8 m of an entry hatch of this Cyclops (the hatch list is cached per Cyclops).</summary>
        bool NearEntryHatch(Transform owner, Vector3 pos)
        {
            return NearHatch(owner, pos, 8f);
        }

        // the root of a thing the network moves (a follower) that this collider belongs to; null for terrain, bases and the like
        Transform OwnerOf(Collider col)
        {
            Transform o;
            if (_ownerCache.TryGetValue(col, out o)) return o;
            var f = col.GetComponentInParent<EntityFollower>();
            o = f != null && !f.Dead ? f.transform : null;
            _ownerCache[col] = o;
            return o;
        }

        void RememberClean(Vector3 pos)
        {
            // clean, and next to a network-moved thing: keep the place in that thing's own space, so it moves along with it
            float r = _cc.radius + 1.5f;
            var c = _cc.transform.TransformPoint(_cc.center);
            int n = Physics.OverlapSphereNonAlloc(c, r, _hits, ~0, QueryTriggerInteraction.Ignore);
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i];
                if (col == null) continue;
                var o = OwnerOf(col);
                if (o == null) continue;
                _goodLocal[o] = o.InverseTransformPoint(pos);
                _goodTime[o] = now;
            }
        }

        void PruneGood(float now)
        {
            List<Transform> dead = null;
            foreach (var kv in _goodTime)
                if (kv.Key == null || now - kv.Value > 30f) { if (dead == null) dead = new List<Transform>(); dead.Add(kv.Key); }
            if (dead == null) return;
            foreach (var k in dead) { _goodTime.Remove(k); _goodLocal.Remove(k); }
        }

        // ------------------------------------------------------------------ helpers

        void Place(Vector3 world)
        {
            try
            {
                bool en = _cc.enabled;
                _cc.enabled = false;                    // a controller ignores a direct position change while it is on
                _cc.transform.position = world;
                _cc.enabled = en;
            }
            catch (Exception e) { SyncKit.Warn("player guard: could not move the player: " + e.Message); }
        }

        void Note(string m)
        {
            if (Time.unscaledTime < _nextLog) return;
            _nextLog = Time.unscaledTime + 2f;
            SyncKit.Info("player guard: " + m);
        }

        static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }
}
