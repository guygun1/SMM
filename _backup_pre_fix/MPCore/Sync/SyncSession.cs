using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MPCore.IO;
using MPCore.Net;

namespace MPCore.Sync
{
    /// <summary>
    /// One multiplayer session. The host is just a normal peer that also relays and arbitrates; a headless host is a
    /// host whose game never creates a local player. There is no separate server code path.
    /// Single threaded: call Tick() from the game's main thread.
    /// </summary>
    public sealed class SyncSession : IDisposable
    {
        public const int ProtocolVersion = 2;
        public const int All = -1;

        public bool IsHost { get; private set; }
        public int LocalPeer { get; private set; } = -1;
        public bool Ready { get; private set; }
        /// <summary>True while remote state is being written into game objects. Game patches must not re-broadcast then.</summary>
        public bool ApplyingRemote { get; private set; }

        public ILog Log = new NullLog();
        public string Name = "player";
        public ModInfo[] Mods = new ModInfo[0];
        public int SendIntervalMs = 50;
        public int AuditIntervalMs = 2000;
        /// <summary>Repair a synced field a local mod overwrote on a non-owner by writing the owner's value back.</summary>
        public bool AutoCorrect = true;
        /// <summary>Unity adapter replaces this (destroyed UnityEngine.Object compares equal to null only via its operator).</summary>
        public Func<object, bool> IsAlive = o => o != null;
        public Func<NetObject, int, bool> OwnershipPolicy = (o, p) => true;

        /// <summary>Remote spawn. The adapter must create the thing and Bind its components.</summary>
        public event Action<NetObject> SpawnReceived;
        public event Action<NetObject> Despawned;
        public event Action<NetObject, int> OwnerChanged;
        public event Action<NetObject, Tracked> StateApplied;
        public event Action<int, string> PeerJoined;
        public event Action<int> PeerLeft;
        public event Action Joined;
        public event Action<string> Rejected;
        /// <summary>Client: the host's mod list differs from ours. Reconnect with AcceptModDifferences = true to join anyway.</summary>
        public event Action<List<ModDiff>> ModsDiffer;
        /// <summary>Client: join even if mods differ (the player confirmed it).</summary>
        public bool AcceptModDifferences;
        /// <summary>Host: mods that must match exactly, whatever the joiner says (the multiplayer mod itself).</summary>
        public HashSet<string> RequiredMods = new HashSet<string>();
        public event Action Disconnected;
        public event Action<DesyncReport> Desync;

        public readonly Dictionary<DesyncKind, int> DesyncCounts = new Dictionary<DesyncKind, int>();

        readonly ITransport _t;
        readonly Dictionary<uint, NetObject> _objs = new Dictionary<uint, NetObject>();
        readonly Dictionary<int, PeerInfo> _peers = new Dictionary<int, PeerInfo>();
        readonly Dictionary<uint, Action<int, ByteReader>> _handlers = new Dictionary<uint, Action<int, ByteReader>>();
        readonly Dictionary<ulong, List<byte[]>> _pending = new Dictionary<ulong, List<byte[]>>();
        readonly Dictionary<string, long> _reportedAt = new Dictionary<string, long>();
        readonly ByteWriter _scratch = new ByteWriter();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        int _pendingCount;
        uint _counter;
        long _lastSend, _lastAudit;
        const int MaxPending = 8192;

        SyncSession(ITransport t, bool host)
        {
            _t = t; IsHost = host;
            t.PeerConnected += OnConnected;
            t.PeerDisconnected += OnDisconnected;
            t.Received += OnData;
            if (host) { LocalPeer = 0; Ready = true; }
        }

        public static SyncSession CreateHost(ITransport t) { return new SyncSession(t, true); }
        public static SyncSession CreateClient(ITransport t) { return new SyncSession(t, false); }

        public IEnumerable<NetObject> Objects => _objs.Values;
        public NetObject Find(uint id) { NetObject o; return _objs.TryGetValue(id, out o) ? o : null; }
        public string PeerName(int id) { PeerInfo p; return _peers.TryGetValue(id, out p) ? p.Name : null; }
        public IEnumerable<int> PeerIds => _peers.Where(kv => kv.Value.Ready).Select(kv => kv.Key);

        public void Dispose() { _t.Dispose(); }

        // ------------------------------------------------------------------ public API

        public void Tick()
        {
            _t.Poll();
            if (!Ready) return;
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastSend >= SendIntervalMs) { _lastSend = now; SendDirty(); }
            if (AuditIntervalMs > 0 && now - _lastAudit >= AuditIntervalMs) { _lastAudit = now; SendAudits(); }
        }

        /// <summary>Scene objects exist on every peer already. Both sides call this with the same stable path.</summary>
        public NetObject RegisterSceneObject(string path)
        {
            uint id = 0x80000000u | (Hash.Str(path) & 0x7FFFFFFFu);
            NetObject o;
            if (_objs.TryGetValue(id, out o)) { o.IsScene = true; return o; }
            o = new NetObject { Id = id, Owner = 0, Prefab = null, IsScene = true };
            _objs[id] = o;
            return o;
        }

        /// <summary>Create a networked object owned by us. The caller binds its components afterwards.</summary>
        public NetObject Spawn(string prefab, byte[] data = null)
        {
            if (!Ready) throw new InvalidOperationException("not joined yet");
            uint id = ((uint)LocalPeer << 24) | (++_counter & 0xFFFFFFu);
            var o = new NetObject { Id = id, Owner = LocalPeer, Prefab = prefab, SpawnData = data };
            _objs[id] = o;
            var w = new ByteWriter();
            WriteSpawn(w, o);
            SendUp(w.ToArray());
            return o;
        }

        public void Despawn(NetObject o)
        {
            if (o == null || !_objs.Remove(o.Id)) return;
            if (!IsHost && o.Owner != LocalPeer) { Log.Warn("Despawn refused: not owner of " + o.Id); _objs[o.Id] = o; return; }
            var w = new ByteWriter(); w.U8((byte)Msg.Despawn); w.VarU(o.Id);
            SendUp(w.ToArray());
            Despawned?.Invoke(o);
        }

        public Tracked Bind(NetObject o, string key, object target)
        {
            SyncType st;
            if (!SyncRegistry.TryGet(target.GetType(), out st))
                throw new InvalidOperationException(target.GetType().FullName + " is not registered for sync");
            uint k = Hash.Str(key);
            if (o.Components.ContainsKey(k)) throw new InvalidOperationException("duplicate component key '" + key + "' on object " + o.Id);
            var t = new Tracked { Key = k, KeyName = key, Target = target, Type = st, Last = null };
            o.Components[k] = t;

            List<byte[]> pend;
            ulong pk = PendKey(o.Id, k);
            if (_pending.TryGetValue(pk, out pend))
            {
                _pending.Remove(pk); _pendingCount -= pend.Count;
                foreach (var body in pend) ApplyBody(o, t, body);
            }
            return t;
        }

        public void Unbind(NetObject o, object target)
        {
            foreach (var kv in o.Components.ToList())
                if (ReferenceEquals(kv.Value.Target, target)) o.Components.Remove(kv.Key);
        }

        public void RequestOwnership(NetObject o)
        {
            if (o.Owner == LocalPeer) return;
            if (IsHost) { GrantOwner(o, 0); return; }
            var w = new ByteWriter(); w.U8((byte)Msg.OwnerRequest); w.VarU(o.Id);
            _t.Send(0, w.ToArray());
        }

        readonly HashSet<uint> _unhandled = new HashSet<uint>();
        public void RegisterMessage(string name, Action<int, ByteReader> handler) { _handlers[Hash.Str(name)] = handler; }

        /// <param name="target">All, 0 (host) or a peer id.</param>
        public void SendMessage(string name, ByteWriter payload, int target = All)
        {
            var w = new ByteWriter();
            w.U8((byte)Msg.Custom); w.VarU(Hash.Str(name)); w.VarI(target); w.VarU((ulong)LocalPeer);
            var p = payload.ToArray(); w.Raw(p, 0, p.Length);
            var data = w.ToArray();
            if (!IsHost) { _t.Send(0, data); return; }
            if (target == All) Fanout(data, -1);
            else if (target != 0) _t.Send(target, data);
        }

        // ------------------------------------------------------------------ transport events

        void OnConnected(int peer)
        {
            if (IsHost) { _peers[peer] = new PeerInfo { Id = peer }; return; }
            // client: transport reached the host, introduce ourselves
            var w = new ByteWriter();
            w.U8((byte)Msg.Hello); w.VarU(ProtocolVersion); w.Str(Name);
            w.VarU((ulong)Mods.Length);
            foreach (var m in Mods) { w.Str(m.Id); w.Str(m.Version); w.Bool(m.ClientOnly); }
            var types = SyncRegistry.All();
            w.VarU((ulong)types.Count);
            foreach (var st in types) { w.Str(st.Name); w.VarU(st.SchemaHash); w.Str(st.ModId); }
            w.Bool(AcceptModDifferences);
            _t.Send(0, w.ToArray());
        }

        void OnDisconnected(int peer)
        {
            if (!IsHost)
            {
                bool was = Ready; Ready = false;
                if (was || LocalPeer < 0) Disconnected?.Invoke();
                return;
            }
            PeerInfo info;
            if (!_peers.TryGetValue(peer, out info)) return;
            _peers.Remove(peer);
            if (!info.Ready) return;
            foreach (var o in _objs.Values.Where(x => x.Owner == peer).ToList())
            {
                if (o.IsScene) GrantOwner(o, 0);
                else
                {
                    _objs.Remove(o.Id);
                    var w = new ByteWriter(); w.U8((byte)Msg.Despawn); w.VarU(o.Id);
                    Fanout(w.ToArray(), peer);
                    Despawned?.Invoke(o);
                }
            }
            var pl = new ByteWriter(); pl.U8((byte)Msg.PeerLeft); pl.VarU((ulong)peer);
            Fanout(pl.ToArray(), peer);
            PeerLeft?.Invoke(peer);
        }

        void OnData(int peer, byte[] data)
        {
            try
            {
                var r = new ByteReader(data);
                var m = (Msg)r.U8();
                if (IsHost)
                {
                    PeerInfo info;
                    if (!_peers.TryGetValue(peer, out info)) return;
                    if (m == Msg.Hello) { OnHello(peer, info, r); return; }
                    if (!info.Ready) return; // ignore everything until the handshake passed
                    switch (m)
                    {
                        case Msg.State: OnState(peer, r, data); break;
                        case Msg.Spawn: OnSpawn(peer, r, data); break;
                        case Msg.Despawn: OnDespawnFrom(peer, r, data); break;
                        case Msg.OwnerRequest: OnOwnerRequest(peer, r); break;
                        case Msg.Custom: OnCustomHost(peer, r); break;
                        case Msg.Audit: OnAudit(r); Fanout(data, peer); break;
                        case Msg.Resync: OnResync(peer, r); break;
                    }
                }
                else
                {
                    if (peer != 0) return;
                    switch (m)
                    {
                        case Msg.Welcome: OnWelcome(r); break;
                        case Msg.Reject: { var why = r.Str(); Log.Warn("join rejected: " + why); Rejected?.Invoke(why); break; }
                        case Msg.ModsDiffer:
                            {
                                int n = (int)r.VarU();
                                var list = new List<ModDiff>();
                                for (int i = 0; i < n; i++) list.Add(new ModDiff { ModId = r.Str(), HostVersion = r.Str(), YourVersion = r.Str(), Note = r.Str() });
                                Log.Warn("the host has different mods: " + string.Join("; ", list.Select(d => d.ToString()).ToArray()));
                                ModsDiffer?.Invoke(list);
                                break;
                            }
                        case Msg.Spawn: OnSpawn(0, r, data); break;
                        case Msg.Adopt: OnAdopt(r); break;
                        case Msg.Despawn: OnDespawnFrom(0, r, data); break;
                        case Msg.State: OnState(0, r, data); break;
                        case Msg.OwnerChange: OnOwnerChange(r); break;
                        case Msg.Custom: OnCustomClient(r); break;
                        case Msg.PeerJoined: { int id = (int)r.VarU(); var n = r.Str(); _peers[id] = new PeerInfo { Id = id, Name = n, Ready = true }; PeerJoined?.Invoke(id, n); break; }
                        case Msg.PeerLeft: { int id = (int)r.VarU(); _peers.Remove(id); PeerLeft?.Invoke(id); break; }
                        case Msg.Audit: OnAudit(r); break;
                        case Msg.Resync: OnResync(0, r); break;
                        case Msg.SnapshotDone: Ready = true; Log.Info("joined as peer " + LocalPeer); Joined?.Invoke(); break;
                    }
                }
            }
            catch (Exception e)
            {
                Report(DesyncKind.Malformed, 0, null, null, null, "peer " + peer + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------ handshake

        void OnHello(int peer, PeerInfo info, ByteReader r)
        {
            int proto = (int)r.VarU();
            string name = r.Str();
            var cm = new List<ModInfo>();
            int nm = (int)r.VarU();
            for (int i = 0; i < nm; i++) cm.Add(new ModInfo { Id = r.Str(), Version = r.Str(), ClientOnly = r.Bool() });
            var ct = new Dictionary<string, KeyValuePair<uint, string>>();
            int nt = (int)r.VarU();
            for (int i = 0; i < nt; i++) { string tn = r.Str(); uint h = (uint)r.VarU(); string mod = r.Str(); ct[tn] = new KeyValuePair<uint, string>(h, mod); }

            bool accepts = r.Remaining > 0 && r.Bool();

            string why = proto != ProtocolVersion ? "different multiplayer protocol (yours " + proto + ", host " + ProtocolVersion + "): update the multiplayer mod" : null;
            List<ModDiff> diffs = null;
            if (why == null)
            {
                diffs = ModDifferences(cm, ct);
                var hard = diffs.Where(d => RequiredMods.Contains(d.ModId)).ToList();
                if (hard.Count > 0) why = "the multiplayer mod must be the same version: " + string.Join("; ", hard.Select(d => d.ToString()).ToArray());
            }
            if (why == null && diffs.Count > 0 && !accepts)
            {
                Log.Info(name + " has different mods (" + diffs.Count + "), asking them to confirm");
                var mw = new ByteWriter(); mw.U8((byte)Msg.ModsDiffer); mw.VarU((ulong)diffs.Count);
                foreach (var d in diffs) { mw.Str(d.ModId); mw.Str(d.HostVersion); mw.Str(d.YourVersion); mw.Str(d.Note); }
                _t.Send(peer, mw.ToArray());
                _t.Disconnect(peer);
                return;
            }
            if (why == null && diffs.Count > 0) Log.Warn(name + " joins with different mods: " + string.Join("; ", diffs.Select(d => d.ToString()).ToArray()));
            if (why != null)
            {
                Log.Warn("rejecting " + name + ": " + why);
                var rj = new ByteWriter(); rj.U8((byte)Msg.Reject); rj.Str(why);
                _t.Send(peer, rj.ToArray());
                _t.Disconnect(peer);
                return;
            }

            info.Name = name;
            var w = new ByteWriter();
            w.U8((byte)Msg.Welcome); w.VarU((ulong)peer); w.Str(Name);
            var others = _peers.Values.Where(p => p.Ready).ToList();
            w.VarU((ulong)others.Count);
            foreach (var p in others) { w.VarU((ulong)p.Id); w.Str(p.Name); }
            _t.Send(peer, w.ToArray());

            SendDirty(); // make sure Last mirrors live state of what we own before snapshotting
            foreach (var o in _objs.Values)
            {
                var sw = new ByteWriter();
                if (o.IsScene) { sw.U8((byte)Msg.Adopt); sw.VarU(o.Id); sw.VarU((ulong)o.Owner); }
                else WriteSpawn(sw, o);
                _t.Send(peer, sw.ToArray());
                foreach (var c in o.Components.Values)
                {
                    if (c.Last == null || c.Last.Any(b => b == null)) continue; // nothing synced yet
                    var st = new ByteWriter();
                    st.U8((byte)Msg.State); st.VarU(o.Id); st.VarU(c.Key); st.VarU(c.Type.Id);
                    st.VarU(FullMask(c.Type));
                    foreach (var b in c.Last) st.Raw(b, 0, b.Length);
                    _t.Send(peer, st.ToArray());
                }
            }
            var done = new ByteWriter(); done.U8((byte)Msg.SnapshotDone);
            _t.Send(peer, done.ToArray());

            info.Ready = true;
            var pj = new ByteWriter(); pj.U8((byte)Msg.PeerJoined); pj.VarU((ulong)peer); pj.Str(name);
            Fanout(pj.ToArray(), peer);
            Log.Info(name + " joined as peer " + peer);
            PeerJoined?.Invoke(peer, name);
        }

        /// <summary>Every difference between the host's and the joiner's mods, including synced types whose fields differ.</summary>
        public List<ModDiff> ModDifferences(List<ModInfo> clientMods, Dictionary<string, KeyValuePair<uint, string>> clientTypes)
        {
            var diffs = new List<ModDiff>();
            var hostMods = Mods.Where(m => !m.ClientOnly).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().Version);
            var cMods = clientMods.Where(m => !m.ClientOnly).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().Version);
            foreach (var kv in hostMods)
            {
                string v;
                if (!cMods.TryGetValue(kv.Key, out v)) diffs.Add(new ModDiff { ModId = kv.Key, HostVersion = kv.Value });
                else if (v != kv.Value) diffs.Add(new ModDiff { ModId = kv.Key, HostVersion = kv.Value, YourVersion = v });
            }
            foreach (var kv in cMods)
                if (!hostMods.ContainsKey(kv.Key)) diffs.Add(new ModDiff { ModId = kv.Key, YourVersion = kv.Value });

            var hostTypes = SyncRegistry.All().ToDictionary(t => t.Name);
            foreach (var kv in hostTypes)
            {
                KeyValuePair<uint, string> c;
                if (clientTypes.TryGetValue(kv.Key, out c) && c.Key != kv.Value.SchemaHash && !diffs.Any(d => d.ModId == kv.Value.ModId))
                    diffs.Add(new ModDiff { ModId = kv.Value.ModId, HostVersion = "?", YourVersion = "?", Note = "synced fields of " + kv.Key + " differ" });
            }
            return diffs;
        }

        string CheckCompat(List<ModInfo> clientMods, Dictionary<string, KeyValuePair<uint, string>> clientTypes)
        {
            var d = ModDifferences(clientMods, clientTypes);
            return d.Count == 0 ? null : string.Join("; ", d.Take(6).Select(x => x.ToString()).ToArray());
        }

        void OnWelcome(ByteReader r)
        {
            LocalPeer = (int)r.VarU();
            var hostName = r.Str();
            _peers[0] = new PeerInfo { Id = 0, Name = hostName, Ready = true };
            int n = (int)r.VarU();
            for (int i = 0; i < n; i++) { int id = (int)r.VarU(); _peers[id] = new PeerInfo { Id = id, Name = r.Str(), Ready = true }; }
        }

        // ------------------------------------------------------------------ objects

        static void WriteSpawn(ByteWriter w, NetObject o)
        {
            w.U8((byte)Msg.Spawn); w.VarU(o.Id); w.VarU((ulong)o.Owner); w.Str(o.Prefab); w.Bytes(o.SpawnData);
        }

        void OnSpawn(int from, ByteReader r, byte[] raw)
        {
            uint id = (uint)r.VarU(); int owner = (int)r.VarU(); string prefab = r.Str(); byte[] data = r.Bytes();
            if (IsHost) owner = from; // a client can only spawn things it owns
            if ((id & 0x80000000u) != 0 || (id >> 24) != (uint)owner && IsHost)
            {
                Report(DesyncKind.OwnershipViolation, id, null, null, null, "peer " + from + " spawned id outside its range");
                return;
            }
            if (_objs.ContainsKey(id)) return;
            var o = new NetObject { Id = id, Owner = owner, Prefab = prefab, SpawnData = data };
            _objs[id] = o;
            if (IsHost) Fanout(raw, from);
            SpawnReceived?.Invoke(o);
        }

        void OnAdopt(ByteReader r)
        {
            uint id = (uint)r.VarU(); int owner = (int)r.VarU();
            NetObject o;
            if (!_objs.TryGetValue(id, out o)) { o = new NetObject { Id = id, IsScene = true }; _objs[id] = o; }
            o.Owner = owner;
        }

        void OnDespawnFrom(int from, ByteReader r, byte[] raw)
        {
            uint id = (uint)r.VarU();
            NetObject o;
            if (!_objs.TryGetValue(id, out o)) return;
            if (IsHost && from != 0 && o.Owner != from)
            {
                Report(DesyncKind.OwnershipViolation, id, null, null, null, "peer " + from + " tried to despawn an object it does not own");
                return;
            }
            _objs.Remove(id);
            if (IsHost) Fanout(raw, from);
            Despawned?.Invoke(o);
        }

        void OnOwnerRequest(int from, ByteReader r)
        {
            NetObject o = Find((uint)r.VarU());
            if (o == null) return;
            if (OwnershipPolicy(o, from)) GrantOwner(o, from);
            else
            {
                var w = new ByteWriter(); w.U8((byte)Msg.OwnerChange); w.VarU(o.Id); w.VarU((ulong)o.Owner);
                _t.Send(from, w.ToArray());
            }
        }

        void GrantOwner(NetObject o, int newOwner)
        {
            if (o.Owner == newOwner) return;
            if (o.Owner == LocalPeer && IsHost) SendDirty(); // flush our last writes before handing over
            o.Owner = newOwner;
            var w = new ByteWriter(); w.U8((byte)Msg.OwnerChange); w.VarU(o.Id); w.VarU((ulong)newOwner);
            Fanout(w.ToArray(), -1);
            OwnerChanged?.Invoke(o, newOwner);
        }

        void OnOwnerChange(ByteReader r)
        {
            NetObject o = Find((uint)r.VarU()); int owner = (int)r.VarU();
            if (o == null || o.Owner == owner) return;
            o.Owner = owner;
            OwnerChanged?.Invoke(o, owner);
        }

        // ------------------------------------------------------------------ state

        static ulong FullMask(SyncType t) { return t.Fields.Length == 64 ? ulong.MaxValue : ((1UL << t.Fields.Length) - 1); }
        static ulong PendKey(uint obj, uint comp) { return ((ulong)obj << 32) | comp; }

        void SendDirty()
        {
            List<Tracked> dead = null;
            foreach (var o in _objs.Values)
            {
                if (o.Owner != LocalPeer) continue;
                foreach (var c in o.Components.Values)
                {
                    if (!IsAlive(c.Target)) { (dead ?? (dead = new List<Tracked>())).Add(c); continue; }
                    var fields = c.Type.Fields;
                    if (c.Last == null) c.Last = new byte[fields.Length][];
                    ulong mask = 0; byte[][] changed = null;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        _scratch.Clear();
                        fields[i].Encode(_scratch, c.Target);
                        if (_scratch.SameAs(c.Last[i])) continue;
                        mask |= 1UL << i;
                        if (changed == null) changed = new byte[fields.Length][];
                        changed[i] = _scratch.ToArray();
                    }
                    if (mask == 0) continue;
                    var w = new ByteWriter();
                    w.U8((byte)Msg.State); w.VarU(o.Id); w.VarU(c.Key); w.VarU(c.Type.Id); w.VarU(mask);
                    for (int i = 0; i < fields.Length; i++)
                        if (changed[i] != null) { w.Raw(changed[i], 0, changed[i].Length); c.Last[i] = changed[i]; }
                    SendUp(w.ToArray());
                }
                if (dead != null) { foreach (var d in dead) o.Components.Remove(d.Key); dead = null; }
            }
        }

        void OnState(int from, ByteReader r, byte[] raw)
        {
            uint id = (uint)r.VarU(), key = (uint)r.VarU(), typeId = (uint)r.VarU();
            var body = r.Rest();
            NetObject o;
            _objs.TryGetValue(id, out o);

            if (IsHost)
            {
                if (o == null) { Report(DesyncKind.Malformed, id, null, null, null, "state for unknown object from peer " + from); return; }
                if (o.Owner != from)
                {
                    Tracked tt; o.Components.TryGetValue(key, out tt);
                    Report(DesyncKind.OwnershipViolation, id, tt?.KeyName, null, tt?.Type.ModId,
                        "peer " + from + " wrote state but owner is " + o.Owner);
                    return;
                }
                Fanout(raw, from);
            }
            else if (o != null && o.Owner == LocalPeer) return; // stale echo after ownership moved

            Tracked c;
            if (o == null || !o.Components.TryGetValue(key, out c)) { AddPending(id, key, body); return; }
            if (c.Type.Id != typeId)
            {
                Report(DesyncKind.Malformed, id, c.KeyName, null, c.Type.ModId, "type mismatch on component");
                return;
            }
            ApplyBody(o, c, body);
        }

        void AddPending(uint id, uint key, byte[] body)
        {
            if (_pendingCount >= MaxPending)
            {
                Report(DesyncKind.UnboundComponent, id, key.ToString(), null, null, "pending state buffer full, dropping");
                return;
            }
            ulong pk = PendKey(id, key);
            List<byte[]> l;
            if (!_pending.TryGetValue(pk, out l)) _pending[pk] = l = new List<byte[]>();
            l.Add(body); _pendingCount++;
        }

        void ApplyBody(NetObject o, Tracked c, byte[] body)
        {
            var r = new ByteReader(body);
            ulong mask = r.VarU();
            var f = c.Type.Fields;
            var vals = new object[f.Length];
            var enc = new byte[f.Length][];
            for (int i = 0; i < f.Length; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                vals[i] = f[i].Codec.Read(r);
                var w = new ByteWriter(); f[i].EncodeValue(w, vals[i]); enc[i] = w.ToArray();
            }
            if (c.Last == null) c.Last = new byte[f.Length][];
            ApplyingRemote = true;
            try
            {
                for (int i = 0; i < f.Length; i++)
                {
                    if (enc[i] == null) continue;
                    f[i].Set(c.Target, vals[i]);
                    c.Last[i] = enc[i];
                }
            }
            finally { ApplyingRemote = false; }
            StateApplied?.Invoke(o, c);
        }

        // ------------------------------------------------------------------ audit: tells "our bug" from "other mod's change"

        void SendAudits()
        {
            foreach (var o in _objs.Values)
            {
                if (o.Owner != LocalPeer) continue;
                foreach (var c in o.Components.Values)
                {
                    if (c.Last == null || c.Last.Any(b => b == null)) continue;
                    var w = new ByteWriter();
                    w.U8((byte)Msg.Audit); w.VarU(o.Id); w.VarU(c.Key); w.VarU(c.Type.Id); w.VarU((ulong)c.Last.Length);
                    foreach (var b in c.Last) w.VarU(Hash.Bytes(b));
                    SendUp(w.ToArray());
                }
            }
        }

        void OnAudit(ByteReader r)
        {
            uint id = (uint)r.VarU(), key = (uint)r.VarU(); r.VarU();
            int n = (int)r.VarU();
            var owner = new uint[n];
            for (int i = 0; i < n; i++) owner[i] = (uint)r.VarU();
            NetObject o; Tracked c;
            if (!_objs.TryGetValue(id, out o) || o.Owner == LocalPeer || !o.Components.TryGetValue(key, out c)) return;
            if (c.Last == null || c.Last.Length != n) return;
            bool needResync = false;
            for (int i = 0; i < n; i++)
            {
                var fld = c.Type.Fields[i];
                if (c.Last[i] == null) { needResync = true; continue; }
                // 1. do we hold what the owner holds?  If not, the network layer lost something: our bug.
                if (Hash.Bytes(c.Last[i]) != owner[i])
                {
                    Report(DesyncKind.LostUpdate, id, c.KeyName, fld.Name, c.Type.ModId, "received value differs from owner's");
                    needResync = true;
                    continue;
                }
                // 2. does the live object still hold what we received?  If not, somebody local overwrote it.
                _scratch.Clear();
                fld.Encode(_scratch, c.Target);
                if (!_scratch.SameAs(c.Last[i]))
                {
                    Report(DesyncKind.LocalOverwrite, id, c.KeyName, fld.Name, c.Type.ModId,
                        "changed locally after sync" + (AutoCorrect ? " (restored)" : ""));
                    if (AutoCorrect)
                    {
                        ApplyingRemote = true;
                        try { fld.Set(c.Target, fld.Codec.Read(new ByteReader(c.Last[i]))); }
                        finally { ApplyingRemote = false; }
                    }
                }
            }
            if (needResync)
            {
                var w = new ByteWriter(); w.U8((byte)Msg.Resync); w.VarU(id); w.VarU(key);
                if (IsHost) { if (o.Owner != 0) _t.Send(o.Owner, w.ToArray()); } else _t.Send(0, w.ToArray());
            }
        }

        void OnResync(int from, ByteReader r)
        {
            uint id = (uint)r.VarU(), key = (uint)r.VarU();
            NetObject o; Tracked c;
            if (!_objs.TryGetValue(id, out o)) return;
            if (IsHost && o.Owner != 0)
            {
                var w = new ByteWriter(); w.U8((byte)Msg.Resync); w.VarU(id); w.VarU(key);
                _t.Send(o.Owner, w.ToArray());
                return;
            }
            if (o.Owner == LocalPeer && o.Components.TryGetValue(key, out c)) c.Last = null; // next SendDirty resends everything
        }

        // ------------------------------------------------------------------ custom messages

        void OnCustomHost(int from, ByteReader r)
        {
            uint name = (uint)r.VarU(); int target = (int)r.VarI(); r.VarU();
            var payload = r.Rest();
            if (target == All || target == 0) Deliver(name, from, payload);
            var w = new ByteWriter();
            w.U8((byte)Msg.Custom); w.VarU(name); w.VarI(target); w.VarU((ulong)from); w.Raw(payload, 0, payload.Length);
            if (target == All) Fanout(w.ToArray(), from);
            else if (target > 0) _t.Send(target, w.ToArray());
        }

        void OnCustomClient(ByteReader r)
        {
            uint name = (uint)r.VarU(); r.VarI(); int sender = (int)r.VarU();
            Deliver(name, sender, r.Rest());
        }

        void Deliver(uint name, int sender, byte[] payload)
        {
            Action<int, ByteReader> h;
            if (_handlers.TryGetValue(name, out h)) h(sender, new ByteReader(payload));
            else if (_unhandled.Add(name)) Log.Warn("custom message " + name.ToString("x8") + " from peer " + sender + " has no handler here (different plugin versions?)");
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Client: to the host. Host: to everyone.</summary>
        void SendUp(byte[] data)
        {
            if (IsHost) Fanout(data, -1); else _t.Send(0, data);
        }

        void Fanout(byte[] data, int except)
        {
            foreach (var p in _peers.Values)
                if (p.Ready && p.Id != except) _t.Send(p.Id, data);
        }

        void Report(DesyncKind kind, uint netId, string comp, string field, string mod, string detail)
        {
            int c; DesyncCounts.TryGetValue(kind, out c); DesyncCounts[kind] = c + 1;
            string key = kind + "/" + netId + "/" + comp + "/" + field;
            long now = _clock.ElapsedMilliseconds, last;
            if (_reportedAt.TryGetValue(key, out last) && now - last < 5000) return; // don't spam the log
            _reportedAt[key] = now;
            var rep = new DesyncReport { Kind = kind, NetId = netId, Component = comp, Field = field, Mod = mod, Detail = detail };
            Log.Warn(rep.ToString());
            Desync?.Invoke(rep);
        }
    }
}
