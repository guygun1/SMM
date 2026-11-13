using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Docking as an explicit event (the way Nitrox does it), instead of every machine running the docking bay's own trigger logic on
    /// a copy of the vehicle that the network moves.
    ///  - The machine that really simulates the vehicle sees the bay's docked vehicle change and sends "dock.ev":
    ///    which vehicle, which sub, which bay of that sub, docked or undocked.
    ///  - The others no longer let a bay react to a network-moved copy (Prefix on VehicleDockingBay.OnTriggerEnter): that second run
    ///    is what replayed the bay's hatch and arm animation and sounds over and over. They apply the event instead: the bay's
    ///    docked vehicle, the vehicle's docked flag and the bay's animator parameter are set once, so the bay plays its animation once.
    /// The pose of the parked vehicle still comes from "ent.dock" (EntitySync), which stays as it was.
    /// Everything is found by name and set through reflection; a member that does not exist in this game build is skipped.
    /// </summary>
    sealed class DockSync : ISyncFeature
    {
        public string Name { get { return "docking"; } }

        static Type _type; static bool _looked;
        internal static Type BayType
        {
            get
            {
                if (!_looked) { _looked = true; try { _type = AccessTools.TypeByName("VehicleDockingBay"); } catch { } }
                return _type;
            }
        }

        static DockSync _inst;
        readonly Dictionary<int, string> _state = new Dictionary<int, string>();   // bay instance id -> id of the vehicle docked in it ("" = none)
        readonly HashSet<int> _undocking = new HashSet<int>();                       // bays whose arms are still opening for an undock we applied
        Component[] _bays = new Component[0];
        float _nextScan, _nextRefresh;

        public void Register()
        {
            _inst = this; _state.Clear(); _undocking.Clear(); _bays = new Component[0]; _nextScan = _nextRefresh = 0f;
            SyncKit.On("dock.ev", OnEvent);
            SyncKit.On("dock.anim", OnFinalAnims);
        }

        // ------------------------------------------------------------------ send: a bay's docked vehicle changed

        // ------------------------------------------------------------------ keep a Cyclops from being thrown

        Component[] _subs = new Component[0]; float _nextSubs;
        const float MaxSubSpeed = 30f;      // a Cyclops tops out near 11 m/s

        /// <summary>
        /// A Cyclops that is simulated here and gets pushed by something overlapping it (a vehicle leaving or entering its bay, or the network copy of one)
        /// is thrown by the depenetration impulse: it flew far away and was then pulled back by the network. Any speed no Cyclops can reach is cleared.
        /// </summary>
        void ClampSubs(float now)
        {
            if (now >= _nextSubs)
            {
                _nextSubs = now + 5f;
                var l = new List<Component>();
                try { foreach (var s in UnityEngine.Object.FindObjectsOfType<SubRoot>()) if (s != null && !s.isBase) l.Add(s); } catch { }
                _subs = l.ToArray();
            }
            for (int i = 0; i < _subs.Length; i++)
            {
                var s = _subs[i]; if (s == null) continue;
                var rb = s.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue;
                if (rb.velocity.sqrMagnitude > MaxSubSpeed * MaxSubSpeed || rb.angularVelocity.sqrMagnitude > 12f * 12f)
                {
                    DebugLog.Log("dock", "cleared an impossible speed of '" + s.name + "': " + rb.velocity.magnitude.ToString("F0") + " m/s", 3);
                    rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                }
            }
        }

        /// <summary>For a few seconds the vehicle and the sub's hull do not collide, so docking and undocking cannot push either of them.</summary>
        static void IgnoreHull(Component vehicle, SubRoot sub, float seconds)
        {
            try
            {
                if (vehicle == null || sub == null) return;
                var a = vehicle.GetComponentsInChildren<Collider>(true);
                var b = sub.GetComponentsInChildren<Collider>(true);
                if (a.Length == 0 || b.Length == 0 || a.Length * b.Length > 12000) return;
                foreach (var x in a) foreach (var y in b) if (x != null && y != null && x != y) Physics.IgnoreCollision(x, y, true);
                if (seconds > 0f && Plugin.I != null) Plugin.I.StartCoroutine(RestoreHull(a, b, seconds));
            }
            catch { }
        }

        static IEnumerator RestoreHull(Collider[] a, Collider[] b, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            foreach (var x in a) foreach (var y in b) { try { if (x != null && y != null) Physics.IgnoreCollision(x, y, false); } catch { } }
        }

        public void Tick()
        {
            if (!SyncKit.InSession || Player.main == null) return;
            try { ClampSubs(Time.unscaledTime); } catch { }
            if (BayType == null) return;
            float now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + 0.25f;
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + 5f;
                var list = new List<Component>();
                try { foreach (var o in UnityEngine.Object.FindObjectsOfType(BayType)) { var c = o as Component; if (c != null) list.Add(c); } } catch { }
                _bays = list.ToArray();
            }
            for (int i = 0; i < _bays.Length; i++)
            {
                var bay = _bays[i];
                if (bay == null) continue;
                int key = bay.GetInstanceID();
                if (_undocking.Contains(key)) continue;                                        // an undock we are applying: do not echo it back
                var veh = DockedVehicle(bay);
                string cur = veh != null ? (SyncKit.IdOf(veh) ?? "") : "";
                string prev;
                if (!_state.TryGetValue(key, out prev)) { _state[key] = cur; continue; }      // first look: the state the world loaded with
                if (cur == prev) continue;
                _state[key] = cur;
                bool docked = cur.Length > 0;
                string vid = docked ? cur : prev;
                var sub = bay.GetComponentInParent<SubRoot>();
                {
                    Component vc = veh;
                    if (vc == null && !string.IsNullOrEmpty(prev)) { var pg = SyncKit.Find(prev); if (pg != null) vc = pg.GetComponent<Vehicle>(); }
                    // docking: the vehicle sits inside the hull colliders, so they stay ignored until it leaves. Undocking: ignored while it drives out.
                    if (vc != null && sub != null) IgnoreHull(vc, sub, docked ? 0f : 8f);
                }
                // the local sound lock of this bay opens for the new event
                SoundWatch.RearmBay(bay.transform.position);
                if (!SyncKit.ShouldSend || sub == null || string.IsNullOrEmpty(vid)) continue;
                if (docked)
                {
                    var f = veh.GetComponent<EntityFollower>();
                    if (f != null && !f.Dead) continue;                                         // a copy the network moves: its owner announces it
                }
                string subId = SyncKit.IdOf(sub);
                if (string.IsNullOrEmpty(subId)) continue;
                int idx = BayIndex(sub, bay);
                bool d = docked;
                SyncKit.Send("dock.ev", w => { w.Str(vid); w.Str(subId); w.VarU((ulong)idx); w.U8(d ? (byte)1 : (byte)0); });
                DebugLog.Log("dock", "sent " + (docked ? "DOCKED " : "UNDOCKED ") + vid + " in bay " + idx + " of '" + sub.name + "'", 4);
                if (docked && Plugin.I != null) Plugin.I.StartCoroutine(SendFinalAnims(vid, veh.gameObject));
            }
        }

        // The vehicle's own animators finish a moment after it docks (the hatch closes behind the driver). The copies on the other machines
        // were released from the network follower when they docked, so nothing updates them any more: they kept the hatch open. This sends
        // the final state of every animator once, 2.5 s after the dock, and the receivers set it.
        static IEnumerator SendFinalAnims(string vid, GameObject veh)
        {
            yield return new WaitForSeconds(2.5f);
            if (veh == null || !SyncKit.InSession) yield break;
            var anims = veh.GetComponentsInChildren<Animator>(true);
            if (anims.Length > 250) yield break;
            var one = new ByteWriter(128); int n = 0;
            for (int i = 0; i < anims.Length; i++)
            {
                byte[] data = null;
                try { data = anims[i] != null && anims[i].runtimeAnimatorController != null ? EntitySync.EncodeAnim(anims[i], anims[i].parameters) : null; } catch { }
                if (data == null || data.Length == 0) continue;
                one.VarU((ulong)i); one.Bytes(data); n++;
            }
            if (n == 0) yield break;
            var body = one.ToArray();
            SyncKit.Send("dock.anim", w => { w.Str(vid); w.VarU((ulong)n); w.Raw(body, 0, body.Length); });
            DebugLog.Log("dock", "sent final animator state of " + vid + " (" + n + " animators)", 4);
        }

        static void OnFinalAnims(int from, ByteReader r)
        {
            string vid = r.Str(); int n = (int)r.VarU();
            if (n < 0 || n > 250 || string.IsNullOrEmpty(vid)) return;
            var go = SyncKit.Find(vid);
            if (go == null || vid == EntitySync.DrivenId()) return;
            var anims = go.GetComponentsInChildren<Animator>(true);
            for (int k = 0; k < n; k++)
            {
                int i = (int)r.VarU(); byte[] data = r.Bytes();
                if (i < 0 || i >= anims.Length || anims[i] == null || anims[i].runtimeAnimatorController == null) continue;
                try { SyncKit.Apply(() => PlayerAvatar.ApplyDamped(anims[i], anims[i].parameters, data, 10f)); } catch { }
            }
            DebugLog.Log("dock", "applied final animator state of " + vid, 4);
        }

        static MethodInfo _gdv; static FieldInfo _dvField; static bool _dvLooked;
        static Component DockedVehicle(Component bay)
        {
            if (!_dvLooked)
            {
                _dvLooked = true;
                try
                {
                    var t = BayType;
                    _gdv = AccessTools.Method(t, "GetDockedVehicle");
                    _dvField = AccessTools.Field(t, "dockedVehicle");
                }
                catch { }
            }
            try { if (_gdv != null) return _gdv.Invoke(bay, null) as Component; } catch { }
            try { if (_dvField != null) return _dvField.GetValue(bay) as Component; } catch { }
            return null;
        }

        static int BayIndex(SubRoot sub, Component bay)
        {
            try
            {
                var all = sub.GetComponentsInChildren(BayType, true);
                for (int i = 0; i < all.Length; i++) if (all[i] == bay) return i;
            }
            catch { }
            return 0;
        }

        static Component BayAt(SubRoot sub, int idx)
        {
            try
            {
                var all = sub.GetComponentsInChildren(BayType, true);
                if (all.Length == 0) return null;
                return idx >= 0 && idx < all.Length ? all[idx] : all[0];
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ receive

        static void OnEvent(int from, ByteReader r)
        {
            string vid = r.Str(), subId = r.Str();
            int idx = (int)r.VarU();
            bool docked = r.U8() != 0;
            if (_inst == null || string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(subId) || BayType == null) return;
            var sub = SyncKit.Find<SubRoot>(subId);
            var vgo = SyncKit.Find(vid);
            if (sub == null || vgo == null) { DebugLog.Log("dock", "event for " + vid + " / " + subId + ": not found here", 4); return; }
            var bay = BayAt(sub, idx);
            var veh = vgo.GetComponent<Vehicle>();
            if (bay == null || veh == null) return;
            if (vid == EntitySync.DrivenId()) return;                                            // we drive it ourselves: our own bay logic decides
            _inst.Apply(sub, bay, veh, vid, docked);
        }

        void Apply(SubRoot sub, Component bay, Vehicle veh, string vid, bool docked)
        {
            int key = bay.GetInstanceID();
            SoundWatch.RearmBay(bay.transform.position);
            IgnoreHull(veh, sub, docked ? 0f : 8f);
            DebugLog.Log("dock", "applying " + (docked ? "DOCKED " : "UNDOCKED ") + vid + " in '" + sub.name + "'", 4);
            if (docked)
            {
                SyncKit.Apply(() =>
                {
                    var f = veh.GetComponent<EntityFollower>();
                    if (f != null) f.Release();
                    SetMember(bay, "dockedVehicle", veh);
                    SetMember(veh, "docked", true);
                    SetMember(bay, "vehicle_docked_param", true);                                // the bay opens and closes its arms once, by itself
                    try { sub.BroadcastMessage("UnlockDoors", SendMessageOptions.DontRequireReceiver); } catch { }
                });
                _state[key] = vid;
                return;
            }
            try { SyncKit.Apply(() => sub.BroadcastMessage("OnLaunchBayOpening", SendMessageOptions.DontRequireReceiver)); } catch { }
            _state[key] = ""; _undocking.Add(key);
            if (Plugin.I != null) Plugin.I.StartCoroutine(FinishUndock(sub, bay, veh, key));
        }

        // the arms need a moment to open before the bay lets go of the vehicle
        IEnumerator FinishUndock(SubRoot sub, Component bay, Vehicle veh, int key)
        {
            yield return new WaitForSeconds(2f);
            if (bay == null) { _undocking.Remove(key); yield break; }
            SyncKit.Apply(() =>
            {
                SetMember(bay, "vehicle_docked_param", false);
                SetMember(bay, "dockedVehicle", null);
                if (veh != null) SetMember(veh, "docked", false);
                try { Traverse.Create(bay).Method("SetVehicleUndocked").GetValue(); } catch { }
            });
            _state[key] = ""; _undocking.Remove(key);
        }

        static void SetMember(object o, string name, object value)
        {
            if (o == null) return;
            try
            {
                var t = Traverse.Create(o);
                var f = t.Field(name);
                if (f.FieldExists()) { f.SetValue(value); return; }
                var p = t.Property(name);
                if (p.PropertyExists()) p.SetValue(value);
            }
            catch (Exception e) { DebugLog.Log("dock", "could not set " + name + ": " + e.Message, 3); }
        }
    }

    /// <summary>
    /// A docking bay reacts to a vehicle that enters its trigger. On a machine where that vehicle is only a copy moved by the network,
    /// the bay must not: its owner docks it and announces that (DockSync), so the bay's animation and sounds run once, not twice.
    /// </summary>
    [HarmonyPatch]
    static class P_BayIgnoresCopies
    {
        static MethodBase Method() { var t = DockSync.BayType; return t == null ? null : AccessTools.Method(t, "OnTriggerEnter", new[] { typeof(Collider) }); }
        static bool Prepare() { return Method() != null; }
        static MethodBase TargetMethod() { return Method(); }
        static bool Prefix(Collider __0)
        {
            try
            {
                if (!SyncKit.InSession || __0 == null) return true;
                var f = __0.GetComponentInParent<EntityFollower>();
                if (f != null && !f.Dead) return false;
            }
            catch { }
            return true;
        }
    }
}
