using System;
using System.Collections;
using HarmonyLib;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Keeps the shared world in step once everybody started from the same save (see WorldTransfer):
    /// time of day, items picked up from the world and items dropped into it.
    /// Objects are matched by the game's own UniqueIdentifier ids, which are identical on every machine that loaded the same save.
    /// </summary>
    static class WorldSync
    {
        static SyncSession _s;
        static bool _remote;       // true while applying something that came from the network, so it is not sent back
        static float _nextTod;

        public static void Attach(SyncSession s)
        {
            _s = s;
            _dropsInFlight.Clear(); _lastDropId = null;
            s.RegisterMessage("tod", OnTime);
            s.RegisterMessage("tod.set", OnTimeSet);
            _lastT = -1; _holdTodUntil = 0f;
            s.RegisterMessage("pk", OnPickup);
            s.RegisterMessage("dr", OnDrop);
        }

        public static void Detach() { _s = null; }

        static void Log(string m) { if (Plugin.I != null) Plugin.I.Log.Info("world sync: " + m); }

        static double _lastT = -1; static float _lastReal, _holdTodUntil;

        static float DaySpeed(DayNightCycle d)
        {
            try { var v = HarmonyLib.Traverse.Create(d).Property("dayNightSpeed").GetValue<float>(); if (v > 0f && v < 1000f) return v; } catch { }
            try { var v = HarmonyLib.Traverse.Create(d).Field("_dayNightSpeed").GetValue<float>(); if (v > 0f && v < 1000f) return v; } catch { }
            return 1f;
        }

        /// <summary>Time jumps made on this machine (the /day and /night commands, sleeping): returns the new time, or null if the clock just ran on.</summary>
        static double? LocalJump(DayNightCycle d, float now)
        {
            double t = d.timePassedAsDouble;
            double? jump = null;
            if (_lastT >= 0 && !SyncKit.Applying)
            {
                // called every frame: the clock advances by the scaled frame time (a paused game does not advance it), so a step of
                // more than twenty seconds in one frame is a jump (/day, /night, sleeping), not the clock running
                double expected = _lastT + Time.deltaTime * DaySpeed(d);
                if (Math.Abs(t - expected) > 20.0) jump = t;
            }
            _lastT = t; _lastReal = now;
            return jump;
        }

        /// <summary>Everybody: notice a jump of the clock made here. The host tells everybody what time it is every couple of seconds.</summary>
        public static void Tick()
        {
            if (_s == null || Player.main == null || DayNightCycle.main == null) return;
            float now = Time.unscaledTime;
            var d = DayNightCycle.main;
            if (SyncKit.Active && HostSettings.SharedTimeOfDay)
            {
                var jump = LocalJump(d, now);
                if (jump.HasValue)
                {
                    Log("the clock was jumped here (" + jump.Value.ToString("F0") + "): telling everybody");
                    if (_s.IsHost) { _nextTod = 0f; }
                    else
                    {
                        _holdTodUntil = now + 4f;        // the host's repeat that is already on its way must not undo it
                        var jw = new ByteWriter(); jw.F64(jump.Value); _s.SendMessage("tod.set", jw, 0);
                    }
                }
            }
            if (!_s.IsHost) return;
            if (now < _nextTod) return;
            _nextTod = now + 2f;
            if (!SyncKit.Active || !HostSettings.SharedTimeOfDay) return;
            var w = new ByteWriter();
            w.F64(d.timePassedAsDouble);
            _s.SendMessage("tod", w);
        }

        static void OnTimeSet(int from, ByteReader r)
        {
            double t = r.F64();
            if (_s == null || !_s.IsHost || from == 0 || !HostSettings.SharedTimeOfDay) return;
            var d = DayNightCycle.main;
            if (d == null || Player.main == null) return;
            SyncKit.Apply(() => { d.timePassedAsDouble = t; });
            _lastT = d.timePassedAsDouble; _lastReal = Time.unscaledTime;
            _nextTod = 0f;                                   // tell everybody now
            Log("another player jumped the clock: time set to " + t.ToString("F0"));
        }

        static void OnTime(int from, ByteReader r)
        {
            double t = r.F64();
            if (_s == null || _s.IsHost || from != 0 || !HostSettings.SharedTimeOfDay) return;   // only the host's clock counts
            if (Time.unscaledTime < _holdTodUntil) return;
            var d = DayNightCycle.main;
            if (d == null || Player.main == null) return;
            if (Math.Abs(d.timePassedAsDouble - t) > 2.0)
            {
                SyncKit.Apply(() => { d.timePassedAsDouble = t; });
                _lastT = d.timePassedAsDouble; _lastReal = Time.unscaledTime;   // a correction from the host is not a jump of ours
            }
        }

        // ------------------------------------------------------------------ items

        // another feature (storage, building...) is replaying a remote change: its Pickup/Drop calls must not be sent back
        static bool Quiet { get { return _s == null || _remote || SyncKit.Applying || !SyncKit.Active; } }

        public static void LocalPickup(Pickupable p)
        {
            if (Quiet || p == null) return;
            var uid = p.GetComponent<UniqueIdentifier>();
            if (uid == null || string.IsNullOrEmpty(uid.Id)) return;
            var w = new ByteWriter(48);
            w.Str(uid.Id);
            _s.SendMessage("pk", w);
        }

        static void OnPickup(int from, ByteReader r)
        {
            string id = r.Str();
            UniqueIdentifier uid = null;
            if (id == null || !UniqueIdentifier.TryGetIdentifier(id, out uid) || uid == null) return;   // not loaded here: normal
            var go = uid.gameObject;
            if (go == null) return;
            // only a thing lying in the world goes away. If it sits in a container here (inventory, locker), that is
            // the storage feature's business, and destroying it would delete somebody's item.
            var pk = go.GetComponent<Pickupable>();
            if (pk == null || pk.attached) return;
            _remote = true;
            try { SyncKit.Apply(() => UnityEngine.Object.Destroy(go)); }
            finally { _remote = false; }
        }

        static string _lastDropId; static int _lastDropFrame;
        static readonly System.Collections.Generic.HashSet<string> _dropsInFlight = new System.Collections.Generic.HashSet<string>();

        public static void LocalDrop(Pickupable p)
        {
            if (Quiet || p == null) return;
            // a tool in the player's hands is still in their inventory (attached): it is not lying in the world, so nobody else may get a copy of it
            if (p.attached) return;
            var uid = p.GetComponent<UniqueIdentifier>();
            if (uid == null || string.IsNullOrEmpty(uid.Id)) return;
            if (uid.Id == _lastDropId && Time.frameCount - _lastDropFrame < 3) return;   // both Drop overloads ran for one drop
            _lastDropId = uid.Id; _lastDropFrame = Time.frameCount;
            var w = new ByteWriter(64);
            SyncKit.Tech(w, p.GetTechType());
            w.Str(uid.Id);
            var t = p.transform;
            w.F32(t.position.x); w.F32(t.position.y); w.F32(t.position.z);
            w.F32(t.rotation.x); w.F32(t.rotation.y); w.F32(t.rotation.z); w.F32(t.rotation.w);
            _s.SendMessage("dr", w);
        }

        static void OnDrop(int from, ByteReader r)
        {
            var tech = SyncKit.Tech(r);
            string id = r.Str();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            if (Plugin.I == null || Player.main == null) return;
            if (id != null && !_dropsInFlight.Add(id)) return;   // the same drop twice: spawn once
            Plugin.I.StartCoroutine(SpawnDrop(tech, id, pos, rot));
        }

        static IEnumerator DeployLater(Constructor c)
        {
            yield return null;
            if (c == null) yield break;
            try { SyncKit.Apply(() => AccessTools.Method(typeof(Constructor), "Deploy").Invoke(c, new object[] { true })); }
            catch (Exception e) { Log("deploy failed: " + e.Message); }
        }

        static IEnumerator SpawnDrop(TechType tech, string id, Vector3 pos, Quaternion rot)
        {
            var task = CraftData.GetPrefabForTechTypeAsync(tech, false);
            yield return task;
            var prefab = task.GetResult();
            if (prefab == null) { Log("no prefab for " + tech); if (id != null) _dropsInFlight.Remove(id); yield break; }
            UniqueIdentifier existing = null;
            if (id != null && UniqueIdentifier.TryGetIdentifier(id, out existing) && existing != null)
            {
                var pk = existing.GetComponent<Pickupable>();
                if (pk == null || !pk.attached) { _dropsInFlight.Remove(id); yield break; }   // already lying in the world here
                // the same id is in an inventory here (copied with the save): spawn the world copy anyway
            }
            _remote = true;
            try
            {
                SyncKit.Apply(() =>
                {
                    var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    var uid = go.GetComponent<UniqueIdentifier>();
                    if (uid != null && id != null) uid.Id = id;   // same id everywhere, so a later pickup finds it
                    go.SetActive(true);
                    // a Mobile Vehicle Bay deploys itself when the owner drops it; do the same here
                    var ctor = go.GetComponent<Constructor>();
                    if (ctor != null && Plugin.I != null) Plugin.I.StartCoroutine(DeployLater(ctor));
                });
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("remote drop failed: " + e.Message); }
            finally { _remote = false; if (id != null) _dropsInFlight.Remove(id); }
        }
    }

    static partial class WorldSyncHelpers { }

    [HarmonyPatch(typeof(Pickupable), "Pickup", new[] { typeof(bool) })]
    static class P_Pickup { static void Postfix(Pickupable __instance) { try { WorldSync.LocalPickup(__instance); } catch { } } }

    [HarmonyPatch(typeof(Pickupable), "Drop", new Type[0])]
    static class P_Drop0 { static void Postfix(Pickupable __instance) { try { WorldSync.LocalDrop(__instance); } catch { } } }

    [HarmonyPatch(typeof(Pickupable), "Drop", new[] { typeof(Vector3), typeof(Vector3), typeof(bool) })]
    static class P_Drop { static void Postfix(Pickupable __instance) { try { WorldSync.LocalDrop(__instance); } catch { } } }
}
