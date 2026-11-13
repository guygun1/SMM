using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Habitat Builder sync.
    ///
    /// Messages:
    ///   build.place   a new ghost was placed (any buildable). Receiver spawns the same prefab with the same unique id.
    ///   build.amount  construction progress of one Constructable (sent from Tick, ~7 times a second while it changes).
    ///   build.state   Constructable.SetState (ghost completed / a finished piece went back to a ghost).
    ///   build.remove  a ghost was fully deconstructed (its Constructable was destroyed while not constructed).
    ///   build.decon   a finished BASE piece (BaseDeconstructable) was turned into a ghost; carries the new ghost's id.
    ///
    /// Non-base placeables (Constructable but not ConstructableBase: lockers, fabricators, solar panels, ...):
    ///   fully synced. The copy is Instantiate(prefab) + uid.Id = id + SetState(false, true), parented to the same
    ///   SubRoot modules root when the original was placed inside a base / Cyclops (position sent in that root's local space).
    ///
    /// Base pieces (ConstructableBase: corridors, rooms, hatches, windows, ...). What the game does (from IL):
    ///   Builder.TryPlace -> BaseGhost.Place() (RebuildGeometry of the ghost Base, RegisterBaseGhost) -> parent to
    ///   BaseGhost.TargetBase -> Constructable.SetState(false). Completing: ConstructableBase.SetState(true) ->
    ///   BaseGhost.Finish() (Base.CopyFrom merges the ghost cells into the target Base). Deconstructing a finished piece:
    ///   BaseDeconstructable.Deconstruct() -> BaseGhost.Deconstruct() builds a ghost Base from the remaining cells.
    ///   All of it is derived from the aim ray and the existing Base, there is no serialisable "add cell X" call.
    ///   What is tried here (the most reliable thing without a running game): REPLAY the placement. The receiver
    ///   instantiates the prefab, creates a fake camera transform at the sender's aim position/rotation, calls
    ///   BaseGhost.UpdatePlacement(fakeCamera, ...) so the ghost computes the same cell/offset from the same (identical)
    ///   world, then VERIFIES that the resulting position is within 0.25 m of the sender's and that BaseGhost.TargetOffset
    ///   equals the sender's. Only then BaseGhost.Place() is called. Any mismatch destroys the ghost and logs a warning,
    ///   so a failed replay never edits the local base. Completion then replays ConstructableBase.SetState(true) (Finish),
    ///   and deconstruction of finished pieces replays BaseDeconstructable.Deconstruct() found by IdAndPath.
    ///   Not done: syncing the ghost's rotation state beyond Builder.lastRotation, Base cell-type edge cases
    ///   (moonpool, observatory, ladders / bulkhead faces), late joiners (ghosts placed before join come with the save).
    /// </summary>
    sealed class BuildingSync : ISyncFeature
    {
        public string Name { get { return "building"; }  }

        // ------------------------------------------------------------------ shared state

        // set around game calls that create / convert a Constructable so we can find out which one it was
        internal static bool Capturing;
        internal static bool InTryPlace;
        internal static Constructable Captured;
        internal static string DeconId, DeconPath;

        // instance ids of objects destroyed by a remote message (their OnDestroy runs later, outside Apply)
        static readonly HashSet<int> RemoteDestroyed = new HashSet<int>();
        // messages that arrived before the ghost they are about was spawned (spawn is asynchronous)
        static readonly Dictionary<string, List<Action<Constructable>>> Pending = new Dictionary<string, List<Action<Constructable>>>();
        static readonly HashSet<string> Spawning = new HashSet<string>();
        // placements that arrived before a player existed (world still loading); replayed from Tick
        static readonly List<Action> DeferredPlace = new List<Action>();
        const int DeferredMax = 128;

        // the ghost produced by the last BaseDeconstructable.Deconstruct postfix. Read and cleared only by OnDecon,
        // because the postfix clears Captured before OnDecon runs.
        internal static Constructable LastCaptured;
        // set when the application is quitting: objects destroyed then are not player actions
        internal static bool ShuttingDown;
        static bool _quitHooked;

        sealed class Watch { public float Last; public float Idle; }
        static readonly Dictionary<Constructable, Watch> Watched = new Dictionary<Constructable, Watch>();
        static float _next;

        public void Register()
        {
            Watched.Clear(); Pending.Clear(); Spawning.Clear(); RemoteDestroyed.Clear(); DeferredPlace.Clear();
            Capturing = false; InTryPlace = false; Captured = null; LastCaptured = null;
            if (!_quitHooked)
            {
                _quitHooked = true;
                try { Application.quitting += OnQuitting; } catch { }
            }
            SyncKit.On("build.place", OnPlace);
            SyncKit.On("build.amount", OnAmount);
            SyncKit.On("build.state", OnState);
            SyncKit.On("build.remove", OnRemove);
            SyncKit.On("build.decon", OnDecon);
        }

        // ------------------------------------------------------------------ sender side helpers

        internal static void Note(Constructable c)
        {
            try
            {
                if (c == null || !SyncKit.ShouldSend) return;
                if (Capturing) return;
                Watch w;
                if (!Watched.TryGetValue(c, out w)) Watched[c] = w = new Watch { Last = c.amount, Idle = 0f };
                w.Idle = 0f;
            }
            catch { }
        }

        static void OnQuitting() { ShuttingDown = true; }

        public void Tick()
        {
            // replay placements that were received before the player existed
            if (DeferredPlace.Count > 0 && Player.main != null)
            {
                var deferred = DeferredPlace.ToArray();
                DeferredPlace.Clear();
                foreach (var act in deferred) { try { act(); } catch (Exception e) { SyncKit.Warn("building: deferred place: " + e.Message); } }
            }
            if (Watched.Count == 0 || Time.unscaledTime < _next) return;
            float dt = 0.15f;
            _next = Time.unscaledTime + dt;
            List<Constructable> dead = null;
            List<KeyValuePair<Constructable, Watch>> list = new List<KeyValuePair<Constructable, Watch>>(Watched);
            foreach (var kv in list)
            {
                var c = kv.Key; var w = kv.Value;
                if (c == null) { if (dead == null) dead = new List<Constructable>(); dead.Add(c); continue; }
                float a = c.amount;
                if (Math.Abs(a - w.Last) > 0.004f)
                {
                    w.Last = a; w.Idle = 0f;
                    if (SyncKit.ShouldSend)
                    {
                        string id = SyncKit.IdOf(c);
                        if (id != null) SyncKit.Send("build.amount", ww => { ww.Str(id); ww.F32(a); });
                    }
                }
                else
                {
                    w.Idle += dt;
                    if (w.Idle > 4f) { if (dead == null) dead = new List<Constructable>(); dead.Add(c); }
                }
            }
            if (dead != null) foreach (var d in dead) Watched.Remove(d);
        }

        static string Uid(Constructable c)
        {
            var uid = c.GetComponent<UniqueIdentifier>();
            return uid != null && !string.IsNullOrEmpty(uid.Id) ? uid.Id : null;
        }

        /// <summary>Called after Builder.TryPlace succeeded. c is the Constructable that was just put into ghost state.</summary>
        internal static void SendPlaced(Constructable c)
        {
            string id = Uid(c);
            if (id == null) return;
            var tech = c.techType;
            bool isBase = c is ConstructableBase;
            var t = c.transform;
            Vector3 pos = t.position; Quaternion rot = t.rotation;

            // parent: inside a base / Cyclops the game parents placeables to the SubRoot's modules root
            byte kind = 0; string subId = ""; Vector3 lp = pos; Quaternion lr = rot;
            if (!isBase)
            {
                var sub = c.GetComponentInParent<SubRoot>();
                string sid = sub != null ? SyncKit.IdOf(sub) : null;
                if (sub != null && sid != null)
                {
                    var mr = sub.GetModulesRoot();
                    if (mr != null)
                    {
                        kind = 1; subId = sid;
                        lp = mr.InverseTransformPoint(pos);
                        lr = Quaternion.Inverse(mr.rotation) * rot;
                    }
                }
            }
            bool inside = false;
            try { inside = c.IsInside(); } catch { }

            var aim = Builder.GetAimTransform();
            Vector3 aimPos = aim != null ? aim.position : pos;
            Quaternion aimRot = aim != null ? aim.rotation : Quaternion.identity;
            int lastRot = 0;
            try { lastRot = Builder.lastRotation; } catch { }

            int ox = 0, oy = 0, oz = 0; bool hasTarget = false;
            if (isBase)
            {
                var bg = c.GetComponent<BaseGhost>() ?? c.GetComponentInChildren<BaseGhost>(true);
                if (bg != null)
                {
                    var off = bg.TargetOffset;
                    ox = off[0]; oy = off[1]; oz = off[2];
                    hasTarget = bg.TargetBase != null;
                }
            }

            SyncKit.Send("build.place", w =>
            {
                SyncKit.Tech(w, tech);
                w.Str(id);
                w.Bool(isBase);
                SyncKit.Vec(w, kind == 1 ? lp : pos);
                SyncKit.Rot(w, kind == 1 ? lr : rot);
                w.U8(kind);
                w.Str(subId);
                w.Bool(inside);
                SyncKit.Vec(w, aimPos);
                SyncKit.Rot(w, aimRot);
                w.VarI(lastRot);
                w.VarI(ox); w.VarI(oy); w.VarI(oz);
                w.Bool(hasTarget);
            });
            SyncKit.Info("building: sent place " + tech + " " + id + (isBase ? " (base piece)" : ""));
            Note(c);
        }

        // ------------------------------------------------------------------ receiver side

        static void RunOrQueue(string id, Action<Constructable> act)
        {
            var c = SyncKit.Find<Constructable>(id);
            if (c != null) { act(c); return; }
            if (!Spawning.Contains(id)) return;     // not coming, not loaded here: normal
            List<Action<Constructable>> l;
            if (!Pending.TryGetValue(id, out l)) Pending[id] = l = new List<Action<Constructable>>();
            if (l.Count < 64) l.Add(act);
        }

        static void FlushPending(string id, Constructable c)
        {
            List<Action<Constructable>> l;
            if (!Pending.TryGetValue(id, out l)) return;
            Pending.Remove(id);
            foreach (var a in l) { try { a(c); } catch (Exception e) { SyncKit.Warn("building: pending: " + e.Message); } }
        }

        static void OnAmount(int from, ByteReader r)
        {
            string id = r.Str(); float a = r.F32();
            RunOrQueue(id, c => ApplyAmount(c, a));
        }

        static void ApplyAmount(Constructable c, float a)
        {
            if (c == null) return;
            if (c.constructed && a >= 0.99f) return;        // late amount message after the completion message
            if (Mathf.Abs(c.constructedAmount - a) < 0.0005f) return;   // the same progress again: no second material update (it makes the build sound again)
            SyncKit.Apply(() =>
            {
                c.constructedAmount = a;
                try { AccessTools.Method(typeof(Constructable), "UpdateMaterial").Invoke(c, null); } catch { }
            });
        }

        static void OnState(int from, ByteReader r)
        {
            string id = r.Str(); bool value = r.Bool(); float a = r.F32();
            RunOrQueue(id, c =>
            {
                SyncKit.Apply(() =>
                {
                    if (c.constructed != value) c.SetState(value, true);
                    c.constructedAmount = a;
                });
                SyncKit.Info("building: state " + id + " constructed=" + value);
            });
        }

        static void OnRemove(int from, ByteReader r)
        {
            string id = r.Str();
            RunOrQueue(id, c =>
            {
                if (c.constructed) return;                  // we disagree; keep it
                RemoteDestroyed.Add(c.gameObject.GetInstanceID());
                SyncKit.Apply(() =>
                {
                    var cb = c as ConstructableBase;
                    UnityEngine.Object.Destroy(c.gameObject);
                });
                SyncKit.Info("building: removed ghost " + id);
            });
        }

        static void OnDecon(int from, ByteReader r)
        {
            string baseId = r.Str(), path = r.Str(), newId = r.Str();
            var tr = SyncKit.FindChild(baseId, path);
            var bd = tr != null ? tr.GetComponent<BaseDeconstructable>() : null;
            if (bd == null) { SyncKit.Info("building: decon target not found here (" + baseId + "/" + path + ")"); return; }
            LastCaptured = null;
            SyncKit.Apply(() =>
            {
                Capturing = true; Captured = null;
                try { bd.Deconstruct(); }
                finally { Capturing = false; Captured = null; }
            });
            Constructable cap = LastCaptured;      // set by the Deconstruct postfix; only this method clears it
            LastCaptured = null;
            if (cap == null) { SyncKit.Warn("building: replayed BaseDeconstructable.Deconstruct produced no ghost"); return; }
            var uid = cap.GetComponent<UniqueIdentifier>();
            if (uid != null) uid.Id = newId;
            SyncKit.Info("building: replayed base deconstruct, ghost id " + newId);
        }

        static void OnPlace(int from, ByteReader r)
        {
            var tech = SyncKit.Tech(r);
            string id = r.Str();
            bool isBase = r.Bool();
            Vector3 pos = SyncKit.Vec(r); Quaternion rot = SyncKit.Rot(r);
            byte kind = r.U8();
            string subId = r.Str();
            bool inside = r.Bool();
            Vector3 aimPos = SyncKit.Vec(r); Quaternion aimRot = SyncKit.Rot(r);
            int lastRot = (int)r.VarI();
            int ox = (int)r.VarI(), oy = (int)r.VarI(), oz = (int)r.VarI();
            bool hasTarget = r.Bool();
            if (string.IsNullOrEmpty(id) || SyncKit.Find(id) != null || Spawning.Contains(id)) return;   // already here
            if (Plugin.I == null) return;
            var off = new[] { ox, oy, oz };
            if (Player.main == null)
            {
                // the world is still loading: keep the placement and replay it from Tick once a player exists.
                // The id is marked Spawning now, so messages about this piece queue in Pending instead of being dropped.
                if (DeferredPlace.Count >= DeferredMax) { SyncKit.Warn("building: placement queue full, dropped " + tech + " " + id); return; }
                Spawning.Add(id);
                DeferredPlace.Add(() => StartSpawn(tech, id, isBase, pos, rot, kind, subId, inside, aimPos, aimRot, lastRot, off, hasTarget));
                return;
            }
            StartSpawn(tech, id, isBase, pos, rot, kind, subId, inside, aimPos, aimRot, lastRot, off, hasTarget);
        }

        static void StartSpawn(TechType tech, string id, bool isBase, Vector3 pos, Quaternion rot, byte kind, string subId,
            bool inside, Vector3 aimPos, Quaternion aimRot, int lastRot, int[] off, bool hasTarget)
        {
            if (SyncKit.Find(id) != null || Plugin.I == null)
            {
                // arrived meanwhile: nothing to spawn, but the messages queued for it can run now
                Spawning.Remove(id);
                var have = SyncKit.Find<Constructable>(id);
                if (have != null) FlushPending(id, have); else Pending.Remove(id);
                return;
            }
            Spawning.Add(id);
            Plugin.I.StartCoroutine(Spawn(tech, id, isBase, pos, rot, kind, subId, inside, aimPos, aimRot, lastRot, off, hasTarget));
        }

        static IEnumerator Spawn(TechType tech, string id, bool isBase, Vector3 pos, Quaternion rot, byte kind, string subId,
            bool inside, Vector3 aimPos, Quaternion aimRot, int lastRot, int[] off, bool hasTarget)
        {
            GameObject go = null; GameObject aimGo = null; bool ok = false;
            try
            {
                var task = CraftData.GetPrefabForTechTypeAsync(tech, false);
                yield return task;
                var prefab = task.GetResult();
                if (prefab == null || prefab.GetComponent<Constructable>() == null) { SyncKit.Info("building: no constructable prefab for " + tech); yield break; }

                if (!isBase)
                {
                    // ---- plain placeable
                    Transform parent = null;
                    if (kind == 1)
                    {
                        var sub = SyncKit.Find<SubRoot>(subId);
                        if (sub == null) { SyncKit.Info("building: sub " + subId + " not loaded here, skipping " + tech); yield break; }
                        parent = sub.GetModulesRoot();
                    }
                    SyncKit.Apply(() =>
                    {
                        go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                        var uid = go.GetComponent<UniqueIdentifier>();
                        if (uid != null) uid.Id = id;
                        go.SetActive(true);
                        if (parent != null)
                        {
                            go.transform.SetParent(parent, false);
                            go.transform.localPosition = pos;
                            go.transform.localRotation = rot;
                        }
                        var c = go.GetComponent<Constructable>();
                        c.SetState(false, true);
                        c.SetIsInside(inside);
                        if (parent != null) c.ExcludeFromSubParentRigidbody();
                    });
                    ok = true;
                }
                else
                {
                    // ---- base piece: replay the placement and verify it (see class comment)
                    SyncKit.Apply(() =>
                    {
                        go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                        var uid = go.GetComponent<UniqueIdentifier>();
                        if (uid != null) uid.Id = id;
                        go.SetActive(true);
                    });
                    yield return null;      // let Awake/Start run (BaseGhost.Start builds the ghost model)
                    yield return null;
                    var cb = go != null ? go.GetComponent<ConstructableBase>() : null;
                    var bg = go != null ? (go.GetComponent<BaseGhost>() ?? go.GetComponentInChildren<BaseGhost>(true)) : null;
                    if (cb == null || bg == null) { SyncKit.Warn("building: base prefab " + tech + " has no ConstructableBase/BaseGhost"); }
                    else
                    {
                        aimGo = new GameObject("mp_build_aim");
                        aimGo.transform.position = aimPos; aimGo.transform.rotation = aimRot;
                        int prevRot = 0; bool haveRot = false;
                        var setter = AccessTools.PropertySetter(typeof(Builder), "lastRotation");
                        try { prevRot = Builder.lastRotation; haveRot = true; if (setter != null) setter.Invoke(null, new object[] { lastRot }); } catch { }
                        bool found = false, geo = false, placed = false;
                        string why = null;
                        SyncKit.Apply(() =>
                        {
                            try
                            {
                                try { bg.SetupGhost(); } catch { }
                                placed = bg.UpdatePlacement(aimGo.transform, cb.placeMaxDistance, out found, out geo, cb);
                                var o = bg.TargetOffset;
                                float dist = Vector3.Distance(go.transform.position, pos);
                                bool sameOff = o[0] == off[0] && o[1] == off[1] && o[2] == off[2];
                                if (!found && !placed) why = "no placement found";
                                else if (dist > 0.25f) why = "position differs by " + dist.ToString("F2") + " m";
                                else if (!sameOff) why = "target offset differs (" + o[0] + "," + o[1] + "," + o[2] + " vs " + off[0] + "," + off[1] + "," + off[2] + ")";
                                else if ((bg.TargetBase != null) != hasTarget) why = "target base mismatch";
                                if (why == null)
                                {
                                    bg.Place();
                                    if (bg.TargetBase != null) go.transform.SetParent(bg.TargetBase.transform, true);
                                    cb.SetState(false, true);
                                    ok = true;
                                }
                            }
                            catch (Exception e) { why = "exception " + e.Message; }
                        });
                        try { if (haveRot && setter != null) setter.Invoke(null, new object[] { prevRot }); } catch { }
                        if (why != null) SyncKit.Warn("building: base piece " + tech + " " + id + " NOT mirrored: " + why);
                    }
                }
            }
            finally
            {
                if (aimGo != null) UnityEngine.Object.Destroy(aimGo);
                Spawning.Remove(id);
            }
            if (!ok)
            {
                if (go != null) { RemoteDestroyed.Add(go.GetInstanceID()); UnityEngine.Object.Destroy(go); }
                Pending.Remove(id);
                yield break;
            }
            SyncKit.Info("building: spawned remote " + tech + " " + id);
            var made = go != null ? go.GetComponent<Constructable>() : null;
            if (made != null) FlushPending(id, made);
        }

        internal static bool WasRemoteDestroyed(GameObject go)
        {
            return go != null && RemoteDestroyed.Remove(go.GetInstanceID());
        }
    }

    // ====================================================================== patches (all wrapped, never throw)

    [HarmonyPatch(typeof(Builder), "TryPlace")]
    static class P_BuildTryPlace
    {
        static void Prefix()
        {
            try { BuildingSync.InTryPlace = true; BuildingSync.Capturing = true; BuildingSync.Captured = null; } catch { }
        }

        static void Postfix(bool __result)
        {
            Constructable c = null;
            try
            {
                c = BuildingSync.Captured;
                BuildingSync.InTryPlace = false; BuildingSync.Capturing = false; BuildingSync.Captured = null;
                if (!__result || c == null || !SyncKit.ShouldSend) return;
                BuildingSync.SendPlaced(c);
            }
            catch (Exception e) { SyncKit.Warn("building: TryPlace postfix: " + e.Message); }
        }

        // the original threw: the postfix did not run, so reset the flags here or capturing stays on for good
        static Exception Finalizer(Exception __exception)
        {
            try
            {
                if (__exception != null) { BuildingSync.InTryPlace = false; BuildingSync.Capturing = false; BuildingSync.Captured = null; }
            }
            catch { }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Constructable), "SetState")]
    static class P_BuildSetState
    {
        static void Postfix(Constructable __instance, bool value)
        {
            try
            {
                if (BuildingSync.Capturing)
                {
                    if (!value && BuildingSync.Captured == null) BuildingSync.Captured = __instance;
                    return;     // object is announced by build.place / build.decon, not by build.state
                }
                if (!SyncKit.ShouldSend) return;
                string id = SyncKit.IdOf(__instance);
                if (id == null) return;
                float amount = __instance.amount;
                SyncKit.Send("build.state", w => { w.Str(id); w.Bool(value); w.F32(amount); });
                BuildingSync.Note(__instance);
            }
            catch (Exception e) { SyncKit.Warn("building: SetState postfix: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(Constructable), "Construct")]
    static class P_BuildConstruct
    {
        static void Postfix(Constructable __instance) { BuildingSync.Note(__instance); }
    }

    [HarmonyPatch(typeof(Constructable), "UpdateMaterial")]
    static class P_BuildUpdateMaterial
    {
        static void Postfix(Constructable __instance) { BuildingSync.Note(__instance); }
    }

    [HarmonyPatch(typeof(Constructable), "OnDestroy")]
    static class P_BuildDestroy
    {
        static void Postfix(Constructable __instance)
        {
            try
            {
                if (__instance == null) return;
                if (BuildingSync.WasRemoteDestroyed(__instance.gameObject)) return;
                // quitting, or the world is being unloaded (no player): the destruction is not a player's deconstruct
                if (BuildingSync.ShuttingDown || Player.main == null) return;
                if (!SyncKit.ShouldSend || __instance.constructed) return;
                string id = SyncKit.IdOf(__instance);
                if (id == null) return;
                SyncKit.Send("build.remove", w => w.Str(id));
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BaseDeconstructable), "Deconstruct")]
    static class P_BuildBaseDeconstruct
    {
        static void Prefix(BaseDeconstructable __instance)
        {
            try
            {
                BuildingSync.DeconId = null; BuildingSync.DeconPath = null;
                if (SyncKit.ShouldSend)
                {
                    string id, path;
                    if (SyncKit.IdAndPath(__instance, out id, out path)) { BuildingSync.DeconId = id; BuildingSync.DeconPath = path; }
                }
                BuildingSync.Capturing = true; BuildingSync.Captured = null;
            }
            catch { }
        }

        static void Postfix()
        {
            try
            {
                var c = BuildingSync.Captured;
                BuildingSync.Capturing = false; BuildingSync.Captured = null;
                // kept for OnDecon, which replays a remote deconstruct and needs the new ghost after this postfix ran
                BuildingSync.LastCaptured = c;
                string baseId = BuildingSync.DeconId, path = BuildingSync.DeconPath;
                BuildingSync.DeconId = null; BuildingSync.DeconPath = null;
                if (c == null || baseId == null || !SyncKit.ShouldSend) return;
                var uid = c.GetComponent<UniqueIdentifier>();
                if (uid == null || string.IsNullOrEmpty(uid.Id)) return;
                string newId = uid.Id;
                SyncKit.Send("build.decon", w => { w.Str(baseId); w.Str(path ?? ""); w.Str(newId); });
                BuildingSync.Note(c);
                SyncKit.Info("building: sent base deconstruct, ghost " + newId);
            }
            catch (Exception e) { SyncKit.Warn("building: Deconstruct postfix: " + e.Message); }
        }

        // the original threw: the postfix did not run, so drop the capture state here
        static Exception Finalizer(Exception __exception)
        {
            try
            {
                if (__exception != null)
                {
                    BuildingSync.Capturing = false; BuildingSync.Captured = null;
                    BuildingSync.DeconId = null; BuildingSync.DeconPath = null;
                }
            }
            catch { }
            return __exception;
        }
    }
}
