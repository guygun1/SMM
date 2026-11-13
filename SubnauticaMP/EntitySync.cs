using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// How far behind real time remote things are drawn. Shared by moving entities and remote players so that a
    /// player standing in a vehicle stays in the same place relative to it. Raised a little when snapshots arrive
    /// late (starvation), lowered again after a few quiet seconds.
    /// </summary>
    static class NetInterp
    {
        public const float Min = 0.10f, Max = 0.22f, Start = 0.12f;
        // Delay may change by at most this many seconds per second. A step (the old code changed it by 10 ms at once)
        // moves every followed object back or forward in time by that step, which shows as a jerk of the whole vehicle.
        const float MaxRate = 0.02f;
        public static float Delay = Start;
        static float _target = Start, _lastStarve;

        public static void Reset() { Delay = Start; _target = Start; _lastStarve = 0f; }

        public static void Starved()
        {
            float t = Time.unscaledTime;
            if (t - _lastStarve < 0.5f) return;
            _lastStarve = t;
            _target = Mathf.Min(Max, _target + 0.01f);
        }

        /// <summary>Called once a frame: Delay glides towards its target instead of stepping.</summary>
        public static void Tick(float now)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (now - _lastStarve > 5f) _target = Mathf.Max(Min, _target - 0.005f * dt);
            Delay += Mathf.Clamp(_target - Delay, -MaxRate * dt, MaxRate * dt);
        }
    }

    /// <summary>Compact encoding of poses: position in 1/128 m (varint), rotation as smallest-three in 4 bytes, velocity as 3 x int16.</summary>
    static class EntQ
    {
        const float PosScale = 128f, VelScale = 200f, RotMax = 0.70710678f;
        static readonly float[] _c = new float[4];

        public static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        public static void WritePos(ByteWriter w, Vector3 p)
        {
            w.VarI(Mathf.RoundToInt(p.x * PosScale)); w.VarI(Mathf.RoundToInt(p.y * PosScale)); w.VarI(Mathf.RoundToInt(p.z * PosScale));
        }

        public static Vector3 ReadPos(ByteReader r)
        {
            float x = (float)r.VarI() / PosScale, y = (float)r.VarI() / PosScale, z = (float)r.VarI() / PosScale;
            return new Vector3(x, y, z);
        }

        public static void WriteVel(ByteWriter w, Vector3 v) { W16(w, v.x * VelScale); W16(w, v.y * VelScale); W16(w, v.z * VelScale); }

        public static Vector3 ReadVel(ByteReader r)
        {
            float x = R16(r) / VelScale, y = R16(r) / VelScale, z = R16(r) / VelScale;
            return new Vector3(x, y, z);
        }

        static void W16(ByteWriter w, float f)
        {
            int v = Mathf.Clamp(Mathf.RoundToInt(f), -32000, 32000);
            w.U8((byte)(v & 0xFF)); w.U8((byte)((v >> 8) & 0xFF));
        }

        static int R16(ByteReader r)
        {
            int lo = r.U8(); int hi = r.U8();
            return (short)(lo | (hi << 8));
        }

        public static void WriteRot(ByteWriter w, Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-6f) { _c[0] = 0; _c[1] = 0; _c[2] = 0; _c[3] = 1; }
            else { _c[0] = q.x / m; _c[1] = q.y / m; _c[2] = q.z / m; _c[3] = q.w / m; }
            int big = 0; float bv = Mathf.Abs(_c[0]);
            for (int i = 1; i < 4; i++) { float a = Mathf.Abs(_c[i]); if (a > bv) { bv = a; big = i; } }
            float sign = _c[big] < 0f ? -1f : 1f;
            uint packed = (uint)big;
            int shift = 2;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                float v = _c[i] * sign;
                int iv = Mathf.Clamp(Mathf.RoundToInt((v / RotMax * 0.5f + 0.5f) * 1023f), 0, 1023);
                packed |= (uint)iv << shift;
                shift += 10;
            }
            w.U8((byte)(packed & 0xFF)); w.U8((byte)((packed >> 8) & 0xFF)); w.U8((byte)((packed >> 16) & 0xFF)); w.U8((byte)((packed >> 24) & 0xFF));
        }

        public static Quaternion ReadRot(ByteReader r)
        {
            uint b0 = r.U8(), b1 = r.U8(), b2 = r.U8(), b3 = r.U8();
            uint packed = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
            int big = (int)(packed & 3u);
            int shift = 2;
            float sum = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) { _c[i] = 0f; continue; }
                int iv = (int)((packed >> shift) & 1023u);
                shift += 10;
                float v = ((iv / 1023f) - 0.5f) * 2f * RotMax;
                _c[i] = v; sum += v * v;
            }
            _c[big] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
            return new Quaternion(_c[0], _c[1], _c[2], _c[3]);
        }
    }

    /// <summary>
    /// Moving things (creatures, vehicles, the Cyclops) are kept in step between players.
    ///
    /// Authority:
    ///  - the host simulates every creature and every vehicle nobody else drives;
    ///  - a player who drives a vehicle simulates it and is the only one who sends it (the "occ" message says who);
    ///  - whoever receives updates for an object stops simulating it (AI and physics off) and follows the snapshots.
    ///    After a few seconds without updates (or an explicit release) it simulates the object again.
    ///
    /// Wire format ("ent"): sender time in ms, count, then per object: index, flags, position (1/128 m), rotation
    /// (4 bytes), optional velocity. Objects are named once with "ent.def" (index -> unique id + class id); a receiver
    /// that meets an unknown index asks for the table with "ent.req". The send rate depends on how fast the object moves
    /// and how far it is from every player. Receivers keep a short snapshot buffer, draw about 120 ms in the past
    /// and interpolate (cubic Hermite between snapshots), extrapolating at most 0.2 s.
    /// </summary>
    static class EntitySync
    {
        // The tick used to be 0.05 s, which capped every object at 20 messages a second whatever its own interval said.
        // Vehicles and the Cyclops now get a snapshot every 1/30 s while they move; the tick must be finer than that.
        const float TickInterval = 0.02f, VehicleInterval = 1f / 30f;
        const float ScanInterval = 2f, Range = 90f, LeaseSeconds = 3f, KeepAlive = 1.5f, VehicleKeepAlive = 0.4f, NearKeepAlive = 0.6f, VehicleRange = 450f;
        const float SpawnRange = 70f, SpawnGrace = 3f, SpawnAfterWorld = 12f;
        const int MaxPerMessage = 120, DefChunk = 100;

        static SyncSession _s;
        static float _nextTick, _nextScan, _nextCull;
        static readonly List<Tracked> _tracked = new List<Tracked>();
        static readonly Dictionary<string, Tracked> _byId = new Dictionary<string, Tracked>();
        static readonly Dictionary<string, uint> _idxOf = new Dictionary<string, uint>();
        static uint _nextIdx = 1;
        static readonly Dictionary<string, float> _lease = new Dictionary<string, float>();      // id -> time of last remote update
        static readonly Dictionary<string, float> _hostSeen = new Dictionary<string, float>();   // joiner: id -> last time the host mentioned it
        static readonly Dictionary<string, float> _missing = new Dictionary<string, float>();    // id -> first time we saw it mentioned but not found nearby
        static readonly Dictionary<string, float> _cullSuspect = new Dictionary<string, float>();
        static readonly HashSet<string> _spawning = new HashSet<string>();
        static readonly Dictionary<string, byte> _kind = new Dictionary<string, byte>();         // class id -> 1 other, 2 creature, 3 vehicle
        static readonly Dictionary<int, Dictionary<uint, Def>> _defs = new Dictionary<int, Dictionary<uint, Def>>();
        static readonly Dictionary<int, Clock> _clocks = new Dictionary<int, Clock>();
        static readonly Dictionary<int, float> _lastReq = new Dictionary<int, float>();
        static readonly Dictionary<int, float> _lastDefSend = new Dictionary<int, float>();
        static readonly Dictionary<string, int> _occupied = new Dictionary<string, int>();      // vehicle id -> peer driving it
        static readonly Dictionary<string, int> _occCorrections = new Dictionary<string, int>(); // host: refused vehicle id -> the peer that really drives it
        static readonly List<string> _pruneIds = new List<string>();
        static readonly List<Tracked> _announce = new List<Tracked>();
        static readonly List<uint> _released = new List<uint>();
        static readonly List<string> _goneIds = new List<string>();
        static readonly ByteWriter _body = new ByteWriter(2048), _msg = new ByteWriter(2048);

        static int _sentTotal, _gotTotal, _spawned, _culled;
        static float _nextStats, _worldStart = -1f, _hostFirst = -1f, _hostLast = -1f;
        static string _lastDriven;

        sealed class Def { public string Id, ClassId; public EntityFollower F; }
        sealed class Clock { public double Off; }

        sealed class Tracked
        {
            public string Id, ClassId; public uint Idx;
            public Transform T; public Rigidbody Rb;
            public Vector3 SentPos = new Vector3(1e9f, 0, 0), LastKnownPos; public Quaternion SentRot = Quaternion.identity;
            public float SentTime = -1000f, NextDue;
            public bool SentMoving, Announced;
            public float NearestDist;
            public bool Vehicle;   // kind 3: vehicle, Cyclops or other movable (gets the fast send rate)
            public bool Docked;    // parked in a sub: currently not sent, and the receivers were told to release it
            public float NextDock; // next time the docked pose is sent
            public bool Auth;      // this machine is the one that sends this object (set each send pass)
            public Animator[] Anims; public AnimatorControllerParameter[][] AParams; public byte[][] ALast;
            public float NextAnimScan, NextAnim, NextAnimFull;
        }

        public static void Attach(SyncSession s)
        {
            _s = s;
            _tracked.Clear(); _byId.Clear(); _idxOf.Clear(); _nextIdx = 1;
            _lease.Clear(); _hostSeen.Clear(); _missing.Clear(); _cullSuspect.Clear(); _spawning.Clear(); _kind.Clear();
            _defs.Clear(); _clocks.Clear(); _lastReq.Clear(); _lastDefSend.Clear(); _announce.Clear(); _released.Clear(); _goneIds.Clear();
            _occupied.Clear(); _occCorrections.Clear(); _lastDriven = null; _worldStart = -1f; _hostFirst = -1f; _hostLast = -1f;
            _nextTick = _nextScan = _nextCull = 0f;
            NetInterp.Reset();
            s.RegisterMessage("ent", OnEntities);
            s.RegisterMessage("ent.def", OnDefs);
            s.RegisterMessage("ent.req", OnDefRequest);
            s.RegisterMessage("ent.rel", OnRelease);
            s.RegisterMessage("ent.gone", OnGone);
            s.RegisterMessage("ent.dock", OnDock);
            s.RegisterMessage("ent.anim", OnAnim);
            s.RegisterMessage("ent.undock", OnUndock);
            s.RegisterMessage("occ", OnOccupy);
            s.RegisterMessage("occ.all", OnOccupyAll);
            s.PeerLeft += OnPeerLeft;
        }

        public static void Detach()
        {
            _s = null;
            try { foreach (var f in UnityEngine.Object.FindObjectsOfType<EntityFollower>()) f.Release(); } catch { }
            _defs.Clear(); _occupied.Clear(); _dockRb.Clear(); _dockMissing.Clear(); _dockSpawning.Clear();
        }

        static void Log(string m) { if (Plugin.I != null) Plugin.I.Log.Info("entity sync: " + m); }

        static bool HasOthers { get { return _s != null && (_s.IsHost ? _s.PeerIds.Any() : _s.Ready); } }

        static void OnPeerLeft(int peer)
        {
            try
            {
                foreach (var k in _occupied.Where(kv => kv.Value == peer).Select(kv => kv.Key).ToList()) _occupied.Remove(k);
                Dictionary<uint, Def> d;
                if (_defs.TryGetValue(peer, out d))
                {
                    foreach (var def in d.Values) if (def.F != null) def.F.Release();
                    _defs.Remove(peer);
                }
                _clocks.Remove(peer); _lastReq.Remove(peer); _lastDefSend.Remove(peer);
            }
            catch (Exception e) { Log("peer left: " + e.Message); }
        }

        // ------------------------------------------------------------------ what this machine simulates

        static List<Vector3> PlayerPositions()
        {
            var list = new List<Vector3>();
            if (Player.main != null) list.Add(Player.main.transform.position);
            if (Plugin.I != null) list.AddRange(Plugin.I.RemotePlayerPositions());
            return list;
        }

        static float NearestSqr(Vector3 p, List<Vector3> players)
        {
            float best = float.MaxValue;
            for (int i = 0; i < players.Count; i++) { float d = (p - players[i]).sqrMagnitude; if (d < best) best = d; }
            return best;
        }

        /// <summary>Extension points for other files: true = treat the object as a vehicle-like movable (synced like vehicles).</summary>
        public static readonly List<Func<GameObject, bool>> ExtraMovable = new List<Func<GameObject, bool>>();
        /// <summary>Extension points: returns the object the local player is driving/piloting, or null.</summary>
        public static readonly List<Func<GameObject>> ExtraDriven = new List<Func<GameObject>>();

        /// <summary>A Cyclops or any other submarine that is not a base (modded subs too).</summary>
        static bool IsCyclops(GameObject go)
        {
            var sr = go.GetComponent<SubRoot>();
            return sr != null && (sr.isCyclops || !sr.isBase);
        }

        static bool ExtraIsMovable(GameObject go)
        {
            for (int i = 0; i < ExtraMovable.Count; i++)
            {
                try { if (ExtraMovable[i](go)) return true; } catch { }
            }
            return false;
        }

        /// <summary>1 = irrelevant, 2 = creature, 3 = vehicle or Cyclops. Cached per class id so a scan costs little.</summary>
        static byte KindOf(UniqueIdentifier uid)
        {
            string cid = uid.ClassId;
            byte k;
            if (!string.IsNullOrEmpty(cid) && _kind.TryGetValue(cid, out k)) return k;
            var go = uid.gameObject;
            if (go.GetComponent<Creature>() != null) k = 2;
            else if (go.GetComponent<Vehicle>() != null || IsCyclops(go) || go.GetComponent<Constructor>() != null || ExtraIsMovable(go)) k = 3;   // Constructor = Mobile Vehicle Bay: its resting place must be the same everywhere
            else k = 1;
            if (!string.IsNullOrEmpty(cid)) _kind[cid] = k;
            return k;
        }

        /// <summary>True for vehicles, Cyclops and other movables (kind 3). Used by EntityFollower to decide whether to check docking.</summary>
        public static bool IsVehicleKind(GameObject go)
        {
            var uid = go != null ? go.GetComponent<UniqueIdentifier>() : null;
            return uid != null && KindOf(uid) == 3;
        }

        static Type _dockingBayType;
        static bool _dockingBayLooked;

        /// <summary>The docking bay class is looked up by name; if the name is wrong only the docking-bay test is lost, the sub test still works.</summary>
        static Type DockingBayType()
        {
            if (!_dockingBayLooked)
            {
                _dockingBayLooked = true;
                try { _dockingBayType = HarmonyLib.AccessTools.TypeByName("VehicleDockingBay"); }
                catch (Exception e) { Log("docking bay type lookup: " + e.Message); _dockingBayType = null; }
            }
            return _dockingBayType;
        }

        /// <summary>
        /// A vehicle parked in a Cyclops (or another non-base sub), or inside a docking bay. Such a vehicle is a child of the
        /// sub and moves with it, so it must not be sent or followed as an independent object: a world pose sent for it
        /// would fight the parent on every other machine. A base's own docking bay does not move, so it does not count.
        /// The Cyclops itself is never "docked".
        /// </summary>
        public static bool IsDockedVehicle(GameObject go)
        {
            if (go == null || !IsVehicleKind(go)) return false;
            if (go.GetComponent<SubRoot>() != null) return false;
            var sr = go.GetComponentInParent<SubRoot>();
            if (sr != null) return !sr.isBase;
            var bay = DockingBayType();
            return bay != null && go.GetComponentInParent(bay) != null;
        }

        /// <summary>
        /// True for anything inside a vehicle docking bay (a Cyclops bay, a moonpool). Each machine runs the bay's own logic on the vehicles
        /// it can see, so the bay opens its arms and plays its sounds by itself; copying those animator calls over the network as well
        /// replays them late and out of order.
        /// </summary>
        public static bool InDockingBay(Component c)
        {
            var bay = DockingBayType();
            return c != null && bay != null && c.GetComponentInParent(bay) != null;
        }

        /// <summary>Rebuild the list of moving things near any player.</summary>
        static void Scan()
        {
            var players = PlayerPositions();
            var drivenId = LocalDrivenId();
            var before = new Dictionary<string, Tracked>(_byId);
            _tracked.Clear(); _byId.Clear();
            float rr = Range * Range, rrVeh = VehicleRange * VehicleRange;
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (uid == null) continue;
                string id = uid.Id;
                if (string.IsNullOrEmpty(id)) continue;
                byte kind = KindOf(uid);
                if (kind == 1) continue;
                var go = uid.gameObject;
                if (!go.activeInHierarchy) continue;
                // a fish or creature in somebody's inventory (held in the hands) is not a world creature: no copy of it may exist or move elsewhere
                var carried = go.GetComponent<Pickupable>();
                if (carried != null && carried.attached) continue;
                var pos = go.transform.position;
                float nd = NearestSqr(pos, players);
                if (nd > (kind == 3 ? rrVeh : rr)) continue;   // vehicles are followed from much further away: a parked one must be where it is
                bool creature = kind == 2;
                // the host sends everything; a joiner sends the vehicle it drives, and vehicles only it has (built here)
                if (!_s.IsHost && id != drivenId)
                {
                    if (creature || !JoinerOnly(id, pos)) continue;
                }
                if (creature)
                {
                    var lm = go.GetComponent<LiveMixin>();
                    if (lm != null && !lm.IsAlive()) continue;
                }
                Tracked t;
                if (!before.TryGetValue(id, out t) || t.T != go.transform)
                {
                    uint idx;
                    if (!_idxOf.TryGetValue(id, out idx)) { idx = _nextIdx++; _idxOf[id] = idx; }
                    t = new Tracked { Id = id, ClassId = uid.ClassId, Idx = idx, T = go.transform, Rb = go.GetComponent<Rigidbody>(), Vehicle = kind == 3 };
                }
                t.NearestDist = Mathf.Sqrt(nd);
                t.LastKnownPos = pos;
                _tracked.Add(t); _byId[id] = t;
            }
            // things that dropped out of the list
            _released.Clear(); _goneIds.Clear();
            Vector3? hostPlayer = Player.main != null ? Player.main.transform.position : (Vector3?)null;
            foreach (var kv in before)
            {
                if (_byId.ContainsKey(kv.Key)) continue;
                var t = kv.Value;
                UniqueIdentifier still;
                bool exists = UniqueIdentifier.TryGetIdentifier(kv.Key, out still) && still != null;
                if (exists) { _released.Add(t.Idx); continue; }   // out of range, dead or inactive: receivers may simulate it again
                // Gone for real only if it was close to this machine's own player, where nothing is ever streamed out.
                // Far things vanish because this machine unloaded them, which says nothing about the other players' copies.
                if (_s.IsHost && hostPlayer != null && (t.LastKnownPos - hostPlayer.Value).sqrMagnitude < 40f * 40f) _goneIds.Add(kv.Key);
                else _released.Add(t.Idx);
            }
            if (_released.Count > 0)
            {
                var w = new ByteWriter(64); w.VarU((ulong)_released.Count);
                foreach (var i in _released) w.VarU(i);
                _s.SendMessage("ent.rel", w);
            }
            foreach (var id in _goneIds)
            {
                var w = new ByteWriter(48); w.Str(id);
                _s.SendMessage("ent.gone", w);
            }
            if (_missing.Count > 2000) _missing.Clear();
            if (_hostSeen.Count > 8000) _hostSeen.Clear();
            if (_lease.Count > 8000) _lease.Clear();
            // name numbers of objects that no longer exist (a new object with the same id gets a new number and is announced again)
            _pruneIds.Clear();
            foreach (var kv in _idxOf)
            {
                if (_byId.ContainsKey(kv.Key)) continue;
                UniqueIdentifier gone;
                if (!UniqueIdentifier.TryGetIdentifier(kv.Key, out gone) || gone == null) _pruneIds.Add(kv.Key);
            }
            foreach (var id in _pruneIds) _idxOf.Remove(id);
        }

        /// <summary>A vehicle near the host that the host never mentions was made on this machine: tell the others.</summary>
        static bool JoinerOnly(string id, Vector3 pos)
        {
            if (_worldStart < 0f || Time.unscaledTime - _worldStart < 10f) return false;   // give the host time to mention everything
            float seen;
            if (_hostSeen.TryGetValue(id, out seen) && Time.unscaledTime - seen < 6f) return false;
            var hp = Plugin.I != null ? Plugin.I.HostPosition() : null;
            return hp != null && (pos - hp.Value).sqrMagnitude < 60f * 60f;   // far from the host it may simply not be loaded there
        }

        /// <summary>Id of the vehicle or Cyclops the local player drives, or null.</summary>
        internal static string DrivenId() { return LocalDrivenId(); }

        /// <summary>Another player drives this vehicle: their machine is the one that knows its real state.</summary>
        internal static bool OccupiedByOther(string id)
        {
            int peer;
            return id != null && _s != null && _occupied.TryGetValue(id, out peer) && peer != _s.LocalPeer;
        }

        static string LocalDrivenId()
        {
            var p = Player.main;
            if (p == null) return null;
            GameObject go = null;
            if (p.currentMountedVehicle != null) go = p.currentMountedVehicle.gameObject;
            else if (p.isPiloting && p.currentSub != null && !p.currentSub.isBase) go = p.currentSub.gameObject;
            if (go == null)
            {
                for (int i = 0; i < ExtraDriven.Count && go == null; i++)
                {
                    try { go = ExtraDriven[i](); } catch { go = null; }
                }
            }
            if (go == null) return null;
            var uid = go.GetComponent<UniqueIdentifier>();
            return uid != null ? uid.Id : null;
        }

        // ------------------------------------------------------------------ tick

        public static void Tick()
        {
            if (_s == null || Player.main == null || !HasOthers) return;
            float now = Time.unscaledTime;
            if (_worldStart < 0f) _worldStart = now;
            NetInterp.Tick(now);
            var driven = LocalDrivenId();
            if (driven != _lastDriven)
            {
                if (_lastDriven != null) SendOccupy(_lastDriven, false);
                if (driven != null)
                {
                    SendOccupy(driven, true);
                    // it was following someone else's copy until now: hand control to the local driver at once
                    UniqueIdentifier du;
                    if (UniqueIdentifier.TryGetIdentifier(driven, out du) && du != null)
                    {
                        var f = du.GetComponent<EntityFollower>();
                        if (f != null) f.Release();
                    }
                    _lease.Remove(driven);
                }
                _lastDriven = driven;
            }
            if (now >= _nextScan) { _nextScan = now + ScanInterval; try { Scan(); } catch (Exception e) { Log("scan: " + e.Message); } }
            if (now >= _nextTick) { _nextTick = now + TickInterval; try { Send(now, driven); } catch (Exception e) { Log("send: " + e.Message); } }
            if (_occCorrections.Count > 0 && _s.IsHost) FlushOccCorrections();
            if (!_s.IsHost && now >= _nextCull) { _nextCull = now + 3f; try { CullJoinerOnlyCreatures(now); } catch (Exception e) { Log("cull: " + e.Message); } }
            if (now >= _nextStats)
            {
                _nextStats = now + 30f;
                if (_tracked.Count > 0 || _gotTotal > 0)
                    Log("tracking " + _tracked.Count + ", sent " + _sentTotal + ", received " + _gotTotal + ", spawned " + _spawned + ", removed " + _culled + ", delay " + (int)(NetInterp.Delay * 1000) + " ms");
            }
        }

        static void Send(float now, string drivenId)
        {
            // new objects are named before they are used
            _announce.Clear();
            foreach (var t in _tracked) if (!t.Announced) _announce.Add(t);
            if (_announce.Count > 0)
            {
                for (int i = 0; i < _announce.Count; i += DefChunk) SendDefsChunk(_announce, i, SyncSession.All);
                foreach (var t in _announce) t.Announced = true;
            }

            int localPeer = _s.LocalPeer;
            _body.Clear();
            int n = 0;
            for (int i = 0; i < _tracked.Count; i++)
            {
                var t = _tracked[i];
                t.Auth = false;
                if (now < t.NextDue) continue;
                if (t.T == null) continue;
                bool driven = t.Id == drivenId;
                int occ;
                bool hasOcc = _occupied.TryGetValue(t.Id, out occ);
                if (hasOcc && occ != localPeer) continue;           // another player drives it: theirs to send
                if (!driven && !hasOcc)
                {
                    float lease;
                    if (_lease.TryGetValue(t.Id, out lease) && now - lease < LeaseSeconds) continue;   // someone else simulates it
                }

                t.Auth = true;
                // a vehicle parked in a sub moves with the sub: its own world pose must not be sent. Release the copies once.
                if (t.Vehicle && IsDockedVehicle(t.T.gameObject))
                {
                    if (!t.Docked) { t.Docked = true; t.NextDock = 0f; SendRelease(t.Idx); }
                    // the others cannot dock it with the game's own code (nobody pilots their copy), so they hang their copy on the
                    // same sub at the same place; refreshed twice a second
                    if (now >= t.NextDock) { t.NextDock = now + 0.5f; SendDock(t); }
                    t.NextDue = now + 0.1f;
                    continue;
                }
                if (t.Docked) { t.Docked = false; SendUndock(t.Id); }

                var pos = t.T.position; var rot = t.T.rotation;
                if (!EntQ.Finite(pos)) continue;
                float dt = now - t.SentTime;
                bool first = t.SentTime < 0f;
                float dist = first ? 1e9f : (pos - t.SentPos).magnitude;
                float ang = first ? 180f : Quaternion.Angle(rot, t.SentRot);
                // A fast object covers less than 2 cm between two 30 ms sends. Without the speed test it would count as
                // "not moved" and be throttled to one send every 0.1 s, which is the choppy look for slow-ish vehicles.
                bool moved = dist >= 0.02f || ang >= 0.7f || (!first && dt > 0.001f && dist / dt >= 0.05f);
                if (!moved)
                {
                    if (t.SentMoving && dt >= 0.1f) { }                  // it stopped: one more snapshot with zero velocity
                    else if (dt >= (t.Vehicle ? VehicleKeepAlive : (t.NearestDist < 40f ? NearKeepAlive : KeepAlive))) { }   // still-standing things are repeated now and then; vehicles often, so a lost or late packet heals fast
                    else { t.NextDue = now + 0.1f; continue; }
                }

                Vector3 vel = Vector3.zero;
                if (moved && !first && dt > 0.001f && dt < 0.6f && dist < 20f) vel = (pos - t.SentPos) / dt;
                if (t.Rb != null && !t.Rb.isKinematic)
                {
                    var rv = t.Rb.velocity;
                    if (rv.sqrMagnitude > 0.0004f && EntQ.Finite(rv)) vel = rv;
                }
                if (vel.sqrMagnitude > 1600f) vel = vel.normalized * 40f;
                bool hasVel = moved && vel.sqrMagnitude > 0.0025f;
                float speed = vel.magnitude;
                float angRate = first || dt < 0.001f ? 0f : ang / dt;

                float interval;
                if (t.Vehicle)
                {
                    // vehicles and the Cyclops: about 30 snapshots a second while they move, so every viewer has real samples
                    // between which to interpolate; 10 a second while they barely move
                    bool moving = driven || hasOcc || speed > 0.3f || angRate > 20f;
                    interval = moving ? VehicleInterval : 0.1f;
                    // slows down smoothly with distance (1x up to 40 m, 3x at the edge of the range), no steps
                    if (!driven && !hasOcc) interval *= 1f + 2f * Mathf.Clamp01((t.NearestDist - 40f) / (VehicleRange - 40f));
                }
                else
                {
                    if (driven || hasOcc || speed > 6f || angRate > 120f) interval = 0.05f;
                    else if (speed > 1.5f || angRate > 45f) interval = 0.1f;
                    else interval = t.NearestDist < 40f ? 0.12f : 0.2f;   // close to a player: more often
                    if (!driven && !hasOcc) interval *= 1f + 2f * Mathf.Clamp01((t.NearestDist - 30f) / 60f);   // smooth: 1x to 30 m, 3x at 90 m
                }

                t.SentPos = pos; t.SentRot = rot; t.SentTime = now; t.SentMoving = hasVel;
                t.NextDue = now + interval - 0.01f;

                _body.VarU(t.Idx);
                _body.U8(hasVel ? (byte)1 : (byte)0);
                EntQ.WritePos(_body, pos);
                EntQ.WriteRot(_body, rot);
                if (hasVel) EntQ.WriteVel(_body, vel);
                n++;
                if (n >= MaxPerMessage) { Flush(n); n = 0; }
            }
            if (n > 0) Flush(n);
            try { SendAnims(now); } catch (Exception e) { Log("anim send: " + e.Message); }
        }

        /// <summary>Tell the others to stop following one object of ours (same format as the "ent.rel" list from Scan).</summary>
        static void SendRelease(uint idx)
        {
            var w = new ByteWriter(16);
            w.VarU((ulong)1);
            w.VarU((ulong)idx);
            _s.SendMessage("ent.rel", w);
        }

        // ------------------------------------------------------------------ vehicles parked in a Cyclops

        static readonly Dictionary<string, bool> _dockRb = new Dictionary<string, bool>();   // vehicle id -> was its rigidbody kinematic before we parked it here

        /// <summary>A vehicle parked in a sub: its place relative to that sub (the sub moves, so a world pose would be wrong).</summary>
        static void SendDock(Tracked t)
        {
            var go = t.T != null ? t.T.gameObject : null;
            if (go == null) return;
            var sr = go.GetComponentInParent<SubRoot>();
            if (sr == null || sr.isBase) return;
            string subId = SyncKit.IdOf(sr);
            if (string.IsNullOrEmpty(subId)) return;
            var st = sr.transform;
            Vector3 lp = st.InverseTransformPoint(t.T.position);
            Quaternion lr = Quaternion.Inverse(st.rotation) * t.T.rotation;
            var w = new ByteWriter(96);
            w.Str(t.Id); w.Str(subId);
            w.F32(lp.x); w.F32(lp.y); w.F32(lp.z);
            w.F32(lr.x); w.F32(lr.y); w.F32(lr.z); w.F32(lr.w);
            w.Str(t.ClassId ?? "");
            _s.SendMessage("ent.dock", w);
        }

        static void SendUndock(string id)
        {
            var w = new ByteWriter(48);
            w.Str(id);
            _s.SendMessage("ent.undock", w);
        }

        static readonly Dictionary<string, float> _dockMissing = new Dictionary<string, float>();
        static readonly HashSet<string> _dockSpawning = new HashSet<string>();

        static void OnDock(int from, ByteReader r)
        {
            string id = r.Str(), subId = r.Str();
            var lp = new Vector3(r.F32(), r.F32(), r.F32());
            var lr = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            string classId = r.Str() ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(subId) || !EntQ.Finite(lp)) return;
            var sub = SyncKit.Find<SubRoot>(subId);
            if (sub == null || id == LocalDrivenId()) return;
            UniqueIdentifier uid;
            if (UniqueIdentifier.TryGetIdentifier(id, out uid) && uid != null && uid.gameObject != null)
            {
                _dockMissing.Remove(id);
                AttachDock(uid.gameObject, id, sub, lp, lr);
                return;
            }
            // the vehicle does not exist here at all (built or brought after the world was copied): create it in the sub, after a short
            // grace in case the saved copy is still streaming in, so no duplicate is made
            float now = Time.unscaledTime;
            if (string.IsNullOrEmpty(classId) || Plugin.I == null || _dockSpawning.Contains(id)) return;
            if (_worldStart < 0f || now - _worldStart < SpawnAfterWorld) return;
            float first;
            if (!_dockMissing.TryGetValue(id, out first)) { _dockMissing[id] = now; return; }
            if (now - first < SpawnGrace) return;
            _dockMissing.Remove(id);
            _dockSpawning.Add(id);
            Plugin.I.StartCoroutine(SpawnDocked(id, classId, sub, lp, lr));
        }

        static IEnumerator SpawnDocked(string id, string classId, SubRoot sub, Vector3 lp, Quaternion lr)
        {
            var req = UWE.PrefabDatabase.GetPrefabAsync(classId);
            yield return req;
            GameObject prefab;
            if (!req.TryGetPrefab(out prefab) || prefab == null) { _dockSpawning.Remove(id); yield break; }
            UniqueIdentifier existing;
            if (sub == null || (UniqueIdentifier.TryGetIdentifier(id, out existing) && existing != null)) { _dockSpawning.Remove(id); yield break; }
            try
            {
                SyncKit.Apply(() =>
                {
                    var go = UnityEngine.Object.Instantiate(prefab, sub.transform.TransformPoint(lp), sub.transform.rotation * lr);
                    var u = go.GetComponent<UniqueIdentifier>();
                    if (u != null) u.Id = id;
                    go.SetActive(true);
                    _spawned++;
                    AttachDock(go, id, sub, lp, lr);
                });
                Log("created the docked vehicle " + classId + " that was missing here");
            }
            catch (Exception e) { Log("spawn docked " + classId + ": " + e.Message); }
            _dockSpawning.Remove(id);
        }

        static void AttachDock(GameObject go, string id, SubRoot sub, Vector3 lp, Quaternion lr)
        {
            if (IsDockedVehicle(go) && !_dockRb.ContainsKey(id)) return;                  // the game parked it here by itself: leave it
            var f = go.GetComponent<EntityFollower>();
            if (f != null) f.Release();
            var rb = go.GetComponent<Rigidbody>();
            if (!_dockRb.ContainsKey(id)) _dockRb[id] = rb != null && rb.isKinematic;
            if (rb != null)
            {
                var cm = rb.collisionDetectionMode;
                if (cm == CollisionDetectionMode.Continuous || cm == CollisionDetectionMode.ContinuousDynamic) rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.isKinematic = true;
            }
            var tr = go.transform;
            // This runs twice a second for as long as the vehicle stays parked. Touching the transform every time makes the physics
            // engine re-test every trigger around it (docking bay, hatch, sound zones), and those fire their enter events and sounds
            // again. So only move it when it is not already in the right place.
            bool moved = false;
            if (tr.parent != sub.transform) { tr.SetParent(sub.transform, true); moved = true; }
            if ((tr.localPosition - lp).sqrMagnitude > 0.0004f || Quaternion.Angle(tr.localRotation, lr) > 1f)
            {
                tr.localPosition = lp;
                tr.localRotation = lr;
                moved = true;
            }
            if (moved && DebugLog.On) DebugLog.Log("dock", "'" + go.name + "' attached to '" + sub.name + "'", 4);
        }

        static void OnUndock(int from, ByteReader r)
        {
            string id = r.Str();
            bool wasKinematic;
            if (string.IsNullOrEmpty(id) || !_dockRb.TryGetValue(id, out wasKinematic)) return;   // not parked by us: the game handled it
            _dockRb.Remove(id);
            UniqueIdentifier uid;
            if (!UniqueIdentifier.TryGetIdentifier(id, out uid) || uid == null) return;
            var go = uid.gameObject;
            if (go == null) return;
            go.transform.SetParent(null, true);
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = wasKinematic;
        }

        // ------------------------------------------------------------------ animators (creatures, vehicles)

        static int _animStart;
        const int MaxAnimEntities = 40;

        static void RefreshAnims(Tracked t, float now)
        {
            t.NextAnimScan = now + 10f;
            t.Anims = t.T != null ? t.T.GetComponentsInChildren<Animator>(true) : new Animator[0];
            if (t.Anims.Length > 250) Array.Resize(ref t.Anims, 250);
            t.AParams = new AnimatorControllerParameter[t.Anims.Length][];
            t.ALast = new byte[t.Anims.Length][];
            for (int i = 0; i < t.Anims.Length; i++)
            {
                var a = t.Anims[i];
                try { t.AParams[i] = a != null && a.runtimeAnimatorController != null ? a.parameters : null; } catch { t.AParams[i] = null; }
            }
        }

        internal static byte[] EncodeAnim(Animator a, AnimatorControllerParameter[] ps)
        {
            if (a == null || ps == null || ps.Length == 0 || !a.isActiveAndEnabled || !a.isInitialized || a.runtimeAnimatorController == null) return null;
            var ms = new System.IO.MemoryStream(ps.Length * 2);
            foreach (var p in ps)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        { int v = Mathf.RoundToInt(Mathf.Clamp(a.GetFloat(p.nameHash), -1000f, 1000f) * 32f); ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); break; }
                    case AnimatorControllerParameterType.Int:
                        { int v = Mathf.Clamp(a.GetInteger(p.nameHash), -32000, 32000); ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); break; }
                    case AnimatorControllerParameterType.Bool:
                        ms.WriteByte(a.GetBool(p.nameHash) ? (byte)1 : (byte)0); break;
                }
            }
            return ms.ToArray();
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// The animation state (the animator parameters) of every object this machine sends and that is close to a player:
        /// a Prawn Suit's legs and arms, a Seamoth's propeller, a fish's swim cycle. The receivers set the same parameters on their copy.
        /// Only changes are sent, plus everything again every two seconds so a lost message is corrected.
        /// </summary>
        static void SendAnims(float now)
        {
            int count = _tracked.Count;
            if (count == 0) return;
            var body = new ByteWriter(512);
            int entities = 0;
            _animStart = (_animStart + 7) % count;
            for (int k = 0; k < count && entities < MaxAnimEntities; k++)
            {
                var t = _tracked[(_animStart + k) % count];
                if (!t.Auth || t.T == null || t.NearestDist > (t.Vehicle ? 250f : 60f) || now < t.NextAnim) continue;
                t.NextAnim = now + (t.Vehicle ? 0.1f * (1f + 2f * Mathf.Clamp01((t.NearestDist - 40f) / 210f)) : 0.2f);   // vehicles: 10/s near, 3/s at 250 m, smoothly
                if (t.Anims == null || now >= t.NextAnimScan) RefreshAnims(t, now);
                bool full = now >= t.NextAnimFull;
                if (full) t.NextAnimFull = now + 2f;
                var one = new ByteWriter(64);
                int n = 0;
                for (int a = 0; a < t.Anims.Length; a++)
                {
                    var data = EncodeAnim(t.Anims[a], t.AParams[a]);
                    if (data == null || data.Length == 0) continue;
                    if (!full && SameBytes(t.ALast[a], data)) continue;
                    t.ALast[a] = data;
                    one.U8((byte)a); one.Bytes(data); n++;
                }
                if (n == 0) continue;
                body.VarU(t.Idx); body.VarU((ulong)n); body.Raw(one.ToArray(), 0, one.Length);
                entities++;
            }
            if (entities == 0) return;
            var w = new ByteWriter(body.Length + 8);
            w.VarU((ulong)entities); w.Raw(body.ToArray(), 0, body.Length);
            _s.SendMessage("ent.anim", w);
            if (DebugLog.On) DebugLog.Log("anim tx", entities + " entities, " + body.Length + " bytes", 3);
        }

        static void OnAnim(int from, ByteReader r)
        {
            int entities = (int)r.VarU();
            if (entities < 0 || entities > 4096) return;
            var defs = DefsOf(from);
            for (int e = 0; e < entities; e++)
            {
                uint idx = (uint)r.VarU(); int n = (int)r.VarU();
                if (n < 0 || n > 255) return;
                Def d; defs.TryGetValue(idx, out d);
                EntityFollower f = null;
                if (d != null)
                {
                    UniqueIdentifier uid;
                    if (UniqueIdentifier.TryGetIdentifier(d.Id, out uid) && uid != null) f = uid.GetComponent<EntityFollower>();
                }
                for (int i = 0; i < n; i++)
                {
                    int a = r.U8(); var data = r.Bytes();
                    if (f != null && !f.Dead) f.SetAnim(a, data);
                }
            }
        }

        static void Flush(int n)
        {
            _msg.Clear();
            _msg.VarU((ulong)(Time.unscaledTime * 1000.0));
            _msg.VarU((ulong)n);
            var b = _body.ToArray();
            _msg.Raw(b, 0, b.Length);
            _s.SendMessage("ent", _msg);
            _body.Clear();
            _sentTotal += n;
        }

        static void SendDefsChunk(List<Tracked> list, int from, int target)
        {
            int count = Math.Min(DefChunk, list.Count - from);
            if (count <= 0) return;
            var w = new ByteWriter(count * 80 + 8);
            w.VarU((ulong)count);
            for (int i = 0; i < count; i++)
            {
                var t = list[from + i];
                w.VarU(t.Idx); w.Str(t.Id); w.Str(t.ClassId ?? "");
            }
            _s.SendMessage("ent.def", w, target);
        }

        // ------------------------------------------------------------------ receive

        static Dictionary<uint, Def> DefsOf(int peer)
        {
            Dictionary<uint, Def> d;
            if (!_defs.TryGetValue(peer, out d)) { d = new Dictionary<uint, Def>(); _defs[peer] = d; }
            return d;
        }

        static void OnDefs(int from, ByteReader r)
        {
            int n = (int)r.VarU();
            if (n < 0 || n > 4096) return;
            var d = DefsOf(from);
            for (int i = 0; i < n; i++)
            {
                uint idx = (uint)r.VarU(); string id = r.Str(), classId = r.Str();
                if (string.IsNullOrEmpty(id)) continue;
                Def old;
                if (d.TryGetValue(idx, out old) && old.Id == id) { old.ClassId = classId; continue; }
                d[idx] = new Def { Id = id, ClassId = classId };
            }
        }

        static void RequestDefs(int from, float now)
        {
            float last;
            if (_lastReq.TryGetValue(from, out last) && now - last < 1.5f) return;
            _lastReq[from] = now;
            _s.SendMessage("ent.req", new ByteWriter(4), from);
        }

        /// <summary>A peer met an index it does not know: send it every name we use, and who drives what.</summary>
        static void OnDefRequest(int from, ByteReader r)
        {
            float now = Time.unscaledTime;
            float last;
            if (_lastDefSend.TryGetValue(from, out last) && now - last < 1f) return;
            _lastDefSend[from] = now;
            for (int i = 0; i < _tracked.Count; i += DefChunk) SendDefsChunk(_tracked, i, from);
            var pairs = new List<KeyValuePair<string, int>>();
            foreach (var kv in _occupied) if (kv.Value != from) pairs.Add(kv);
            if (_lastDriven != null) pairs.Add(new KeyValuePair<string, int>(_lastDriven, _s.LocalPeer));
            if (pairs.Count > 0)
            {
                var w = new ByteWriter(128); w.VarU((ulong)pairs.Count);
                foreach (var kv in pairs) { w.Str(kv.Key); w.VarU((ulong)kv.Value); }
                _s.SendMessage("occ.all", w, from);
            }
        }

        static void OnOccupyAll(int from, ByteReader r)
        {
            int n = (int)r.VarU();
            if (n < 0 || n > 1024) return;
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(); int peer = (int)r.VarU();
                if (id != null && peer != _s.LocalPeer)
                {
                    _occupied[id] = peer;
                    // we are sitting in a vehicle that somebody else was given first: get out
                    if (id == LocalDrivenId()) ForceExitVehicle();
                }
            }
        }

        /// <summary>The host refused our claim on a vehicle (someone else drives it): leave the seat on this machine too.</summary>
        static void ForceExitVehicle()
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(Player), "ToNormalMode", new[] { typeof(bool) });
                if (m != null && Player.main != null) m.Invoke(Player.main, new object[] { true });
                else SyncKit.Warn("vehicle: someone else drives it, but there is no way to leave the seat here");
                SyncKit.Info("vehicle: already driven by another player, you were moved out");
            }
            catch (Exception e) { SyncKit.Warn("vehicle: could not leave the seat: " + e.Message); }
        }

        /// <summary>
        /// Sender time (ms) to this machine's clock. The offset follows the fastest packets, so jitter does not move the timeline.
        /// The offset may fall by at most 2 ms per packet: a sudden fall would map the next snapshots earlier than the ones
        /// before them, Push would clamp them, and the object would stall and then bunch up (a visible stutter).
        /// </summary>
        static double MapTime(int from, double senderSec, double now)
        {
            double off = now - senderSec;
            Clock c;
            if (!_clocks.TryGetValue(from, out c)) { c = new Clock { Off = off }; _clocks[from] = c; }
            else if (off < c.Off) c.Off = Math.Max(off, c.Off - 0.002);
            else c.Off += (off - c.Off) * 0.01;
            return senderSec + c.Off;
        }

        static void OnEntities(int from, ByteReader r)
        {
            double sms = (double)r.VarU();
            int n = (int)r.VarU();
            if (n < 0 || n > 4096) return;
            float now = Time.unscaledTime;
            double t = MapTime(from, sms / 1000.0, now);
            var defs = DefsOf(from);
            string mine = LocalDrivenId();
            bool unknown = false;
            if (from == 0) { _hostLast = now; if (_hostFirst < 0f) _hostFirst = now; }
            for (int i = 0; i < n; i++)
            {
                uint idx = (uint)r.VarU();
                int flags = r.U8();
                var pos = EntQ.ReadPos(r);
                var rot = EntQ.ReadRot(r);
                var vel = (flags & 1) != 0 ? EntQ.ReadVel(r) : Vector3.zero;
                Def d;
                if (!defs.TryGetValue(idx, out d)) { unknown = true; continue; }
                _gotTotal++;
                try { Receive(from, d, t, now, pos, rot, vel, mine); }
                catch (Exception e) { Log("receive: " + e.Message); }
            }
            if (unknown) RequestDefs(from, now);
        }

        static void Receive(int from, Def d, double t, float now, Vector3 pos, Quaternion rot, Vector3 vel, string mine)
        {
            string id = d.Id;
            if (id == mine) return;                                         // never fight the local driver
            int occ;
            if (_occupied.TryGetValue(id, out occ) && occ != from) return;  // stale data: someone else drives it now
            _lease[id] = now;
            if (from == 0) _hostSeen[id] = now;
            if (Player.main == null) return;

            if (d.F != null)
            {
                if (d.F.Dead) return;   // being released this frame; the next update attaches a fresh follower
                d.F.Push(t, pos, rot, vel);
                return;
            }
            UniqueIdentifier uid;
            if (UniqueIdentifier.TryGetIdentifier(id, out uid) && uid != null)
            {
                _missing.Remove(id);
                var held = uid.GetComponent<Pickupable>();
                if (held != null && held.attached) return;   // in somebody's hands or inventory here: the host's copy must not drag it around
                if (IsDockedVehicle(uid.gameObject)) return; // parked in a sub: it moves with the sub, not with the sender's copy
                var f = uid.GetComponent<EntityFollower>();
                if (f == null) f = uid.gameObject.AddComponent<EntityFollower>();
                if (f.Dead) return;
                d.F = f;
                f.Push(t, pos, rot, vel);
                return;
            }
            MaybeSpawn(d, pos, rot, now);
        }

        /// <summary>
        /// An object that exists on the sender but not here. Spawn it only if it is close to us (so the area is loaded
        /// and a saved copy would exist already), and only after it stayed missing for a few seconds: a streaming cell may
        /// still be loading, and spawning then would make a duplicate.
        /// </summary>
        static void MaybeSpawn(Def d, Vector3 pos, Quaternion rot, float now)
        {
            if (string.IsNullOrEmpty(d.ClassId) || Plugin.I == null || _spawning.Contains(d.Id)) return;
            if (_worldStart < 0f || now - _worldStart < SpawnAfterWorld) return;
            if ((pos - Player.main.transform.position).sqrMagnitude > SpawnRange * SpawnRange) { _missing.Remove(d.Id); return; }
            float first;
            if (!_missing.TryGetValue(d.Id, out first)) { _missing[d.Id] = now; return; }
            if (now - first < SpawnGrace) return;
            _missing.Remove(d.Id);
            _spawning.Add(d.Id);
            Plugin.I.StartCoroutine(SpawnMissing(d, pos, rot));
        }

        static IEnumerator SpawnMissing(Def d, Vector3 pos, Quaternion rot)
        {
            string id = d.Id;
            var req = UWE.PrefabDatabase.GetPrefabAsync(d.ClassId);
            yield return req;
            GameObject prefab;
            if (!req.TryGetPrefab(out prefab) || prefab == null) { _spawning.Remove(id); yield break; }
            UniqueIdentifier existing;
            if (UniqueIdentifier.TryGetIdentifier(id, out existing) && existing != null) { _spawning.Remove(id); yield break; }
            try
            {
                SyncKit.Apply(() =>
                {
                    var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    var uid = go.GetComponent<UniqueIdentifier>();
                    if (uid != null) uid.Id = id;
                    go.SetActive(true);
                    var f = go.AddComponent<EntityFollower>();
                    f.Push(Time.unscaledTime, pos, rot, Vector3.zero);
                    d.F = f;
                    _spawned++;
                });
            }
            catch (Exception e) { Log("spawn " + d.ClassId + ": " + e.Message); }
            _spawning.Remove(id);
        }

        static void OnRelease(int from, ByteReader r)
        {
            int n = (int)r.VarU();
            if (n < 0 || n > 4096) return;
            var defs = DefsOf(from);
            for (int i = 0; i < n; i++)
            {
                uint idx = (uint)r.VarU();
                Def d;
                if (!defs.TryGetValue(idx, out d) || d == null) continue;
                if (d.F != null) d.F.Release();
                _lease.Remove(d.Id);
            }
        }

        // ------------------------------------------------------------------ one driver per vehicle

        static void SendOccupy(string id, bool on)
        {
            var w = new ByteWriter(48); w.Str(id); w.Bool(on);
            _s.SendMessage("occ", w);
            Log((on ? "I drive " : "I left ") + id);
        }

        /// <summary>
        /// Host: the first claim of a vehicle wins. A claim for a vehicle that another peer (or the host) already drives is
        /// refused: it is not recorded here, and the host sends everybody the real driver (occ.all) on the next tick. The
        /// relay has already passed the refused claim on by then, so the correction always arrives after it.
        /// </summary>
        static bool HostAcceptsClaim(string id, int from)
        {
            int winner;
            if (id == _lastDriven || id == LocalDrivenId()) winner = _s.LocalPeer;
            else if (!_occupied.TryGetValue(id, out winner)) return true;
            if (winner == from) return true;
            _occCorrections[id] = winner;
            Log("refused " + from + " for " + id + ", driven by peer " + winner);
            return false;
        }

        static void FlushOccCorrections()
        {
            var w = new ByteWriter(128);
            w.VarU((ulong)_occCorrections.Count);
            foreach (var kv in _occCorrections) { w.Str(kv.Key); w.VarU((ulong)kv.Value); }
            _occCorrections.Clear();
            _s.SendMessage("occ.all", w);
        }

        static void OnOccupy(int from, ByteReader r)
        {
            string id = r.Str(); bool on = r.Bool();
            if (id == null) return;
            if (on)
            {
                if (_s != null && _s.IsHost && !HostAcceptsClaim(id, from)) return;
                _occupied[id] = from; _lease.Remove(id); return;
            }
            int cur;
            if (!_occupied.TryGetValue(id, out cur) || cur != from) return;
            _occupied.Remove(id);
            // the driver left: the host (who simulates it by default) takes over at once instead of waiting for the follower to time out
            if (_s != null && _s.IsHost)
            {
                UniqueIdentifier u;
                if (UniqueIdentifier.TryGetIdentifier(id, out u) && u != null)
                {
                    var f = u.GetComponent<EntityFollower>();
                    if (f != null) f.Release();
                }
                _lease.Remove(id);
            }
        }

        /// <summary>True if another player drives this vehicle right now.</summary>
        public static bool TakenByOther(GameObject go)
        {
            if (_s == null || go == null) return false;
            var uid = go.GetComponentInParent<UniqueIdentifier>();
            if (uid == null) return false;
            // the Cyclops: the chair is inside, the id is on the sub
            var sr = go.GetComponentInParent<SubRoot>();
            if (sr != null && !sr.isBase) uid = sr.GetComponent<UniqueIdentifier>() ?? uid;
            int peer;
            return uid != null && uid.Id != null && _occupied.TryGetValue(uid.Id, out peer) && peer != _s.LocalPeer;
        }

        static void OnGone(int from, ByteReader r)
        {
            string id = r.Str();
            UniqueIdentifier uid;
            if (id == null || !UniqueIdentifier.TryGetIdentifier(id, out uid) || uid == null) return;
            var go = uid.gameObject;
            if (go == null) return;
            var pk = go.GetComponent<Pickupable>();
            if (pk != null && pk.attached) return;                       // carried by someone here
            if (id == LocalDrivenId()) return;
            UnityEngine.Object.Destroy(go);
        }

        /// <summary>
        /// Joiner: creatures close to the host that the host never mentions only exist here. Remove them, but only when the
        /// host is clearly alive and reporting (a frozen or hitching host must not cause a purge), and only after they were
        /// missing on two checks in a row.
        /// </summary>
        static void CullJoinerOnlyCreatures(float now)
        {
            if (_hostFirst < 0f || now - _hostFirst < 12f || now - _hostLast > 2f) { _cullSuspect.Clear(); return; }
            var hostPos = Plugin.I != null ? Plugin.I.HostPosition() : (Vector3?)null;
            if (hostPos == null) return;
            var doomed = new List<GameObject>();
            float r2 = 50f * 50f;
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (uid == null || KindOf(uid) != 2) continue;
                var go = uid.gameObject;
                if ((go.transform.position - hostPos.Value).sqrMagnitude > r2) continue;
                string id = uid.Id;
                if (string.IsNullOrEmpty(id) || _spawning.Contains(id)) continue;
                // the host does not list dead creatures (Scan skips them), so a corpse is never "mentioned": not a ghost, leave it
                var lm = go.GetComponent<LiveMixin>();
                if (lm != null && !lm.IsAlive()) { _cullSuspect.Remove(id); continue; }
                float seen;
                if (_hostSeen.TryGetValue(id, out seen) && now - seen < 6f) { _cullSuspect.Remove(id); continue; }
                var pk = go.GetComponent<Pickupable>();
                if (pk != null && pk.attached) continue;                  // carried fish
                if (go.GetComponent<WaterParkItem>() != null) continue;   // lives in an aquarium
                float since;
                if (!_cullSuspect.TryGetValue(id, out since)) { _cullSuspect[id] = now; continue; }
                if (now - since < 4f) continue;
                _cullSuspect.Remove(id);
                doomed.Add(go);
            }
            foreach (var go in doomed) { if (go != null) { UnityEngine.Object.Destroy(go); _culled++; } }
            if (_cullSuspect.Count > 1000) _cullSuspect.Clear();
        }
    }

    /// <summary>
    /// Put on an object while another machine simulates it: switches off its AI and physics and draws it from a
    /// buffer of received snapshots, a little in the past (see NetInterp), so motion is smooth even when packets
    /// arrive unevenly. Gives control back after a few seconds without updates, or when told to.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    class EntityFollower : MonoBehaviour
    {
        struct Snap { public double T; public Vector3 P, V; public Quaternion R; }

        const float ReleaseAfter = 8f, ExtrapCap = 0.2f, TeleportDist = 30f;

        // 12 snapshots: at 30 a second that is 0.4 s of history, more than the longest delay (NetInterp.Max) plus margin
        readonly Snap[] _b = new Snap[12];
        int _n;
        double _lastRecv, _gap = 0.1;
        Vector3 _shownP; Quaternion _shownR; bool _hasShown;
        readonly List<Behaviour> _disabled = new List<Behaviour>();
        Rigidbody _rb; bool _rbWasKinematic; RigidbodyInterpolation _rbInterp; bool _taken;
        bool _dockWatch;   // a vehicle: give the object back as soon as it is parked in a sub

        public bool Dead { get; private set; }

        // animator parameters received from the machine that simulates this object
        readonly Dictionary<int, byte[]> _animData = new Dictionary<int, byte[]>();
        Animator[] _anims; AnimatorControllerParameter[][] _aps;
        SubRoot _sr; bool _srLooked;
        CollisionDetectionMode _rbMode; bool _rbModeSet;

        /// <summary>
        /// Hold the body kinematic. A kinematic body only supports Discrete and ContinuousSpeculative collision detection; the game's
        /// vehicles use Continuous, and every switch logged "Kinematic body only supports Speculative Continuous collision detection".
        /// </summary>
        void Kin()
        {
            if (_rb == null) return;
            var m = _rb.collisionDetectionMode;
            if (m == CollisionDetectionMode.Continuous || m == CollisionDetectionMode.ContinuousDynamic)
            {
                if (!_rbModeSet) { _rbMode = m; _rbModeSet = true; }
                _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            _rb.isKinematic = true;
        }

        // The Cyclops's helm display, lighting panel, buttons and screens are animated by the game on each machine from what that player is doing
        // and where they stand (the HUD shows when you are in the sub or at the helm). Copying the host's values over them every frame fought the
        // local game: the joiner's HUD and lights flashed and only the host saw them right.
        static readonly Dictionary<int, bool> _hud = new Dictionary<int, bool>();
        static readonly string[] HudWords = { "HUD", "Helm", "Lighting", "Panel", "Button", "Display", "Holo", "Sonar", "Screen", "Warning", "Status", "Meter", "Ability", "Decoy", "Monitor", "Gauge", "Control", "Terminal", "Console" };
        static bool LocalHud(Animator a)
        {
            if (a == null) return false;
            int id = a.GetInstanceID();
            bool r;
            if (_hud.TryGetValue(id, out r)) return r;
            r = false;
            try
            {
                for (var t = a.transform; t != null && !r; t = t.parent)
                {
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        string tn = c.GetType().Name;
                        // the name / colour panel (ColorNameControl), the lighting panel's buttons and every world-space canvas of the sub show
                        // and hide by distance and by who is looking: each machine drives its own
                        if (tn == "Canvas" || tn.StartsWith("uGUI", StringComparison.Ordinal) || tn.IndexOf("ColorName", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("NameControl", StringComparison.OrdinalIgnoreCase) >= 0) { r = true; break; }
                        if (tn.StartsWith("Cyclops", StringComparison.Ordinal) || tn.StartsWith("Submarine", StringComparison.Ordinal) || tn.StartsWith("Sub", StringComparison.Ordinal))
                            foreach (var w in HudWords) if (tn.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { r = true; break; }
                        if (r) break;
                    }
                    if (t.GetComponent<SubRoot>() != null) break;
                }
            }
            catch { }
            if (_hud.Count > 4000) _hud.Clear();
            _hud[id] = r;
            return r;
        }

        public void SetAnim(int index, byte[] data) { if (index >= 0 && index < 256 && data != null) _animData[index] = data; }

        void Update()
        {
            if (Dead || _animData.Count == 0) return;
            if (!_srLooked) { _srLooked = true; _sr = GetComponent<SubRoot>(); }
            // not while a local entry/exit cinematic runs or the local player is at this Cyclops's hatch (see PlayerGuard.HoldAnims)
            if (SubnauticaMP.Features.PlayerGuard.HoldAnims(_sr, transform)) return;
            if (_anims == null)
            {
                _anims = GetComponentsInChildren<Animator>(true);
                _aps = new AnimatorControllerParameter[_anims.Length][];
                for (int i = 0; i < _anims.Length; i++)
                    try { _aps[i] = _anims[i] != null && _anims[i].runtimeAnimatorController != null ? _anims[i].parameters : null; } catch { _aps[i] = null; }
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            foreach (var kv in _animData)
            {
                int i = kv.Key;
                if (i >= _anims.Length || _anims[i] == null || _aps[i] == null || !_anims[i].isActiveAndEnabled) continue;
                if (_sr != null && LocalHud(_anims[i])) continue;      // helm HUD, lighting panel, buttons: each player's own game drives those
                try { PlayerAvatar.ApplyDamped(_anims[i], _aps[i], kv.Value, dt); } catch { }
            }
        }

        Pickupable _pk; bool _pkLooked;
        /// <summary>A creature that was picked up here (fish in the hands) is no longer ours to move.</summary>
        bool Carried()
        {
            if (!_pkLooked) { _pkLooked = true; _pk = GetComponent<Pickupable>(); }
            return _pk != null && _pk.attached;
        }

        static readonly string[] AiTypes = { "Creature", "CreatureAction", "Locomotion", "SwimBehaviour", "SplineFollowing",
            "StayAtLeashPosition", "SwimRandom", "AvoidObstacles", "AvoidTerrain", "LastTarget", "CreatureDeath", "WorldForces" };

        /// <summary>Add a snapshot. t is the sender's time mapped to this machine's clock (Time.unscaledTime scale).</summary>
        public void Push(double t, Vector3 p, Quaternion r, Vector3 v)
        {
            if (Dead) return;
            if (Carried()) { Release(); return; }
            if (!_taken) Take(p, r);
            if (_n > 0)
            {
                var last = _b[_n - 1];
                if (t <= last.T) t = last.T + 0.001;
                double gap = t - last.T;
                if (gap < 1.0) _gap += (gap - _gap) * 0.2;
                if ((p - last.P).sqrMagnitude > TeleportDist * TeleportDist) { _n = 0; _hasShown = false; }
            }
            if (_n == _b.Length) { Array.Copy(_b, 1, _b, 0, _n - 1); _n--; }
            _b[_n].T = t; _b[_n].P = p; _b[_n].R = r; _b[_n].V = v;
            _n++;
            _lastRecv = Time.unscaledTime;
        }

        void Take(Vector3 p, Quaternion r)
        {
            _taken = true;
            if (DebugLog.On) DebugLog.Log("follow", "take '" + name + "' kinematic-before=" + (GetComponent<Rigidbody>() != null && GetComponent<Rigidbody>().isKinematic), 10);
            foreach (var b in GetComponents<Behaviour>())
            {
                if (b == null || !b.enabled || b == this) continue;
                for (var t = b.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    if (Array.IndexOf(AiTypes, t.Name) >= 0) { b.enabled = false; _disabled.Add(b); break; }
            }
            _rb = GetComponent<Rigidbody>();
            _dockWatch = EntitySync.IsVehicleKind(gameObject);
            if (_rb != null)
            {
                _rbWasKinematic = _rb.isKinematic;
                _rbInterp = _rb.interpolation;
                // The body is moved from FixedUpdate (MovePosition/MoveRotation), not from LateUpdate. Physics only sees
                // a pose change made in a physics step, so a player standing on a moved Cyclops was carried in steps of
                // the physics rate. With Interpolate, the renderer smooths between the physics steps.
                _rb.interpolation = RigidbodyInterpolation.Interpolate;
                Kin();
                _rb.position = p; _rb.rotation = r;
            }
            transform.SetPositionAndRotation(p, r);
            _shownP = p; _shownR = r; _hasShown = true;
            _lastRecv = Time.unscaledTime;
        }

        public void Release()
        {
            if (Dead) return;
            if (DebugLog.On) DebugLog.Log("follow", "release '" + name + "'", 10);
            Dead = true;
            foreach (var b in _disabled) if (b != null) b.enabled = true;
            _disabled.Clear();
            if (_rb != null)
            {
                if (_rbModeSet) { try { _rb.collisionDetectionMode = _rbMode; } catch { } _rbModeSet = false; }
                _rb.isKinematic = _rbWasKinematic;
                _rb.interpolation = _rbInterp;
                if (!_rbWasKinematic && _n > 0) { try { _rb.velocity = _b[_n - 1].V; } catch { } }
            }
            _taken = false;
            Destroy(this);
        }

        void OnDestroy() { Dead = true; }

        // LateUpdate, last in line: after the game's own scripts (vehicles switch their rigidbody back on in Update)
        void LateUpdate()
        {
            if (Dead) return;
            if (Carried()) { Release(); return; }
            // parked in a Cyclops or docking bay: the sub moves it now, so give it back at once (the sender stops sending too)
            if (_dockWatch && EntitySync.IsDockedVehicle(gameObject)) { Release(); return; }
            double now = Time.unscaledTime;
            if (now - _lastRecv > ReleaseAfter) { Release(); return; }
            if (_rb != null)
            {
                // objects with a rigidbody are moved in FixedUpdate (see there); here we only keep them kinematic
                if (!_rb.isKinematic) Kin();
                return;
            }
            if (_n == 0) return;

            Vector3 p; Quaternion q;
            Sample(now - NetInterp.Delay, out p, out q);
            Smooth(p, q, Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            transform.SetPositionAndRotation(_shownP, _shownR);
        }

        // FixedUpdate: objects with a rigidbody are moved here, once per physics step, with MovePosition/MoveRotation.
        // Physics then knows where the body is and carries whatever stands on it (the player in a Cyclops) in the same step.
        // The pose is sampled at the same delayed clock as everything else (NetInterp.Delay). Several physics steps in one
        // frame sample the same time, so the body moves per frame; Interpolate hides that between the steps.
        void FixedUpdate()
        {
            if (Dead || _rb == null || _n == 0) return;
            if (!_rb.isKinematic) Kin();
            Vector3 p; Quaternion q;
            Sample(Time.unscaledTime - NetInterp.Delay, out p, out q);
            if (Smooth(p, q, Time.fixedDeltaTime)) { _rb.position = _shownP; _rb.rotation = _shownR; }   // a teleport: no sliding
            else { _rb.MovePosition(_shownP); _rb.MoveRotation(_shownR); }
        }

        /// <summary>
        /// Low-pass over the sampled pose: hides the small jumps where extrapolation hands over to a new snapshot.
        /// Returns true when the pose was set without smoothing (first pose, or a jump longer than TeleportDist).
        /// </summary>
        bool Smooth(Vector3 p, Quaternion q, float dt)
        {
            if (!_hasShown || (p - _shownP).sqrMagnitude > TeleportDist * TeleportDist)
            {
                _shownP = p; _shownR = q; _hasShown = true;
                return true;
            }
            _shownP = Vector3.Lerp(_shownP, p, 1f - Mathf.Exp(-30f * dt));
            _shownR = Quaternion.Slerp(_shownR, q, 1f - Mathf.Exp(-22f * dt));
            return false;
        }

        void Sample(double rt, out Vector3 p, out Quaternion q)
        {
            var last = _b[_n - 1];
            if (rt >= last.T)
            {
                double ahead = rt - last.T;
                // moving, and the next snapshot is late: draw a bit further in the past from now on
                if (last.V.sqrMagnitude > 0.01f && _gap < 0.2 && ahead > 1.5 * _gap + 0.03 && ahead < 0.5) NetInterp.Starved();
                p = last.P + last.V * (float)Math.Min(ahead, ExtrapCap);
                q = last.R;
                return;
            }
            if (rt <= _b[0].T) { p = _b[0].P; q = _b[0].R; return; }
            int i = 0;
            while (i < _n - 2 && _b[i + 1].T <= rt) i++;   // _b[i].T <= rt < _b[i+1].T
            var a = _b[i]; var c = _b[i + 1];
            float span = (float)(c.T - a.T);
            float u = span > 1e-5f ? (float)((rt - a.T) / (c.T - a.T)) : 1f;
            q = Quaternion.Slerp(a.R, c.R, u);
            if (span > 0.4f || span < 0.004f) { p = Vector3.Lerp(a.P, c.P, u); return; }
            // cubic Hermite with tangents from the neighbouring snapshots (Catmull-Rom): no speed jumps at snapshot boundaries
            Vector3 ma = i > 0 ? (c.P - _b[i - 1].P) / (float)(c.T - _b[i - 1].T) : (c.P - a.P) / span;
            Vector3 mc = i + 2 < _n ? (_b[i + 2].P - a.P) / (float)(_b[i + 2].T - a.T) : (c.P - a.P) / span;
            ma = Vector3.ClampMagnitude(ma, 80f); mc = Vector3.ClampMagnitude(mc, 80f);
            float u2 = u * u, u3 = u2 * u;
            p = (2f * u3 - 3f * u2 + 1f) * a.P + (u3 - 2f * u2 + u) * span * ma + (-2f * u3 + 3f * u2) * c.P + (u3 - u2) * span * mc;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Vehicle), "OnHandClick")]
    static class P_VehicleClick
    {
        static bool Prefix(Vehicle __instance)
        {
            try
            {
                if (!EntitySync.TakenByOther(__instance.gameObject)) return true;
                try { ErrorMessage.AddMessage("Another player is driving this."); } catch { }
                return false;
            }
            catch { return true; }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(PilotingChair), "OnHandClick")]
    static class P_ChairClick
    {
        static bool Prefix(PilotingChair __instance)
        {
            try
            {
                if (!EntitySync.TakenByOther(__instance.gameObject)) return true;
                try { ErrorMessage.AddMessage("Another player is piloting the Cyclops."); } catch { }
                return false;
            }
            catch { return true; }
        }
    }
}
