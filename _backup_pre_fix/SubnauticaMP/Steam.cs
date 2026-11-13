using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MPCore.Net;
using Steamworks;

namespace SubnauticaMP
{
    /// <summary>
    /// Thin, defensive wrapper over the Steamworks.NET that ships with the game. Every call is guarded: the Epic/Xbox
    /// builds have no Steam, and Steam may be closed. Steam join uses Valve's relay network, so no port forwarding is needed.
    /// </summary>
    public static class SteamSupport
    {
        public const string PresenceHostKey = "mp_host";
        const string ConnectPrefix = "+mpjoin ";

        static Callback<GameRichPresenceJoinRequested_t> _joinCb;
        static bool _initTried;

        /// <summary>Set when a friend clicked "Join Game" in Steam or the game was started with +mpjoin. Consumed by the plugin.</summary>
        public static ulong PendingJoin;

        /// <summary>True once Steam was found and our callbacks are hooked.</summary>
        public static bool Initialized { get { return _initTried; } }

        public static bool Available
        {
            get
            {
                try { return SteamAPI.IsSteamRunning() && SteamUser.BLoggedOn(); }
                catch { return false; }
            }
        }

        public static ulong MyId
        {
            get { try { return SteamUser.GetSteamID().m_SteamID; } catch { return 0; } }
        }

        public static string MyName
        {
            get { try { return SteamFriends.GetPersonaName(); } catch { return null; } }
        }

        /// <summary>Hook the join-request callback and read +mpjoin from the command line. Safe to call repeatedly.</summary>
        public static void TryInit(Action<string> log)
        {
            if (_initTried || !Available) return;
            _initTried = true;
            try
            {
                _joinCb = Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequested);
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "+mpjoin") { ulong id; if (ulong.TryParse(args[i + 1], out id)) PendingJoin = id; }
                log?.Invoke("Steam detected as " + MyName + "; Steam join is available.");
            }
            catch (Exception e) { log?.Invoke("Steam init failed: " + e.Message); }
        }

        static void OnJoinRequested(GameRichPresenceJoinRequested_t c)
        {
            var s = c.m_rgchConnect ?? "";
            int i = s.IndexOf(ConnectPrefix, StringComparison.Ordinal);
            ulong id;
            if (i >= 0 && ulong.TryParse(s.Substring(i + ConnectPrefix.Length).Trim(), out id)) PendingJoin = id;
        }

        /// <summary>Tells Steam friends we are hosting, so they get a Join button and appear in the in-game list.</summary>
        public static void AdvertiseHosting(bool on)
        {
            try
            {
                SteamFriends.SetRichPresence(PresenceHostKey, on ? "1" : "");
                SteamFriends.SetRichPresence("connect", on ? ConnectPrefix + MyId : "");
            }
            catch { }
        }

        public static void InviteFriends()
        {
            try { SteamFriends.ActivateGameOverlayInviteDialogConnectString(ConnectPrefix + MyId); } catch { }
        }

        public sealed class FriendInfo
        {
            public ulong Id;
            public string Name;
            public bool Hosting;
        }

        /// <summary>Friends that currently advertise a hosted world. Call every few seconds, not every frame.</summary>
        public static List<FriendInfo> Friends()
        {
            var list = new List<FriendInfo>();
            try
            {
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    SteamFriends.RequestFriendRichPresence(id);
                    bool hosting = SteamFriends.GetFriendRichPresence(id, PresenceHostKey) == "1";
                    list.Add(new FriendInfo { Id = id.m_SteamID, Name = SteamFriends.GetFriendPersonaName(id), Hosting = hosting });
                }
                list.Sort((a, b) => a.Hosting != b.Hosting ? (a.Hosting ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            return list;
        }
    }

    // ------------------------------------------------------------------------------------------------ transports

    static class SteamMsg
    {
        const int Reliable = 8; // k_nSteamNetworkingSend_Reliable

        public static void Send(HSteamNetConnection c, byte[] data)
        {
            IntPtr p = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, p, data.Length);
                long num;
                SteamNetworkingSockets.SendMessageToConnection(c, p, (uint)data.Length, Reliable, out num);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static void Receive(HSteamNetConnection c, Action<byte[]> onMsg)
        {
            var ptrs = new IntPtr[32];
            int n;
            do
            {
                n = SteamNetworkingSockets.ReceiveMessagesOnConnection(c, ptrs, ptrs.Length);
                for (int i = 0; i < n; i++)
                {
                    var m = SteamNetworkingMessage_t.FromIntPtr(ptrs[i]);
                    var buf = new byte[m.m_cbSize];
                    Marshal.Copy(m.m_pData, buf, 0, m.m_cbSize);
                    SteamNetworkingMessage_t.Release(ptrs[i]);
                    onMsg(buf);
                }
            } while (n == ptrs.Length);
        }
    }

    /// <summary>Accepts Steam friends through Steam's relay network. Plugs into MultiHostTransport next to the TCP listener.</summary>
    public sealed class SteamHostTransport : ITransport
    {
        readonly HSteamListenSocket _listen;
        readonly Callback<SteamNetConnectionStatusChangedCallback_t> _cb;
        readonly Dictionary<int, HSteamNetConnection> _conns = new Dictionary<int, HSteamNetConnection>();
        readonly Dictionary<uint, int> _byConn = new Dictionary<uint, int>();
        readonly Queue<Action> _q = new Queue<Action>();
        int _next = 1;

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public SteamHostTransport()
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            if (_listen.m_HSteamListenSocket == 0) throw new InvalidOperationException("Steam refused to open a listen socket");
            _cb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
        }

        void OnStatus(SteamNetConnectionStatusChangedCallback_t c)
        {
            if (c.m_info.m_hListenSocket.m_HSteamListenSocket != _listen.m_HSteamListenSocket) return;
            uint h = c.m_hConn.m_HSteamNetConnection;
            switch (c.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    SteamNetworkingSockets.AcceptConnection(c.m_hConn);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (_byConn.ContainsKey(h)) break;
                    int id = _next++;
                    _conns[id] = c.m_hConn; _byConn[h] = id;
                    _q.Enqueue(() => PeerConnected?.Invoke(id));
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    SteamNetworkingSockets.CloseConnection(c.m_hConn, 0, null, false);
                    int gone;
                    if (_byConn.TryGetValue(h, out gone))
                    {
                        _byConn.Remove(h); _conns.Remove(gone);
                        _q.Enqueue(() => PeerDisconnected?.Invoke(gone));
                    }
                    break;
            }
        }

        public void Send(int peer, byte[] data)
        {
            HSteamNetConnection c;
            if (_conns.TryGetValue(peer, out c)) SteamMsg.Send(c, data);
        }

        public void Disconnect(int peer)
        {
            HSteamNetConnection c;
            if (!_conns.TryGetValue(peer, out c)) return;
            SteamNetworkingSockets.CloseConnection(c, 0, "kicked", true);
            _byConn.Remove(c.m_HSteamNetConnection); _conns.Remove(peer);
            _q.Enqueue(() => PeerDisconnected?.Invoke(peer));
        }

        public void Poll()
        {
            while (_q.Count > 0) _q.Dequeue()();
            foreach (var kv in new List<KeyValuePair<int, HSteamNetConnection>>(_conns))
            {
                int id = kv.Key;
                SteamMsg.Receive(kv.Value, d => Received?.Invoke(id, d));
            }
        }

        public void Dispose()
        {
            foreach (var c in _conns.Values) SteamNetworkingSockets.CloseConnection(c, 0, "host closed", false);
            _conns.Clear(); _byConn.Clear();
            SteamNetworkingSockets.CloseListenSocket(_listen);
            _cb.Dispose();
        }
    }

    /// <summary>Connects to a hosting friend by Steam id through Steam's relay. No IP address or port forwarding involved.</summary>
    public sealed class SteamClientTransport : ITransport
    {
        readonly HSteamNetConnection _conn;
        readonly Callback<SteamNetConnectionStatusChangedCallback_t> _cb;
        readonly Queue<Action> _q = new Queue<Action>();
        bool _closed;

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public SteamClientTransport(ulong hostSteamId)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(hostSteamId));
            _cb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            _conn = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            if (_conn.m_HSteamNetConnection == 0) throw new InvalidOperationException("Steam refused to start the connection");
        }

        void OnStatus(SteamNetConnectionStatusChangedCallback_t c)
        {
            if (c.m_hConn.m_HSteamNetConnection != _conn.m_HSteamNetConnection) return;
            switch (c.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    _q.Enqueue(() => PeerConnected?.Invoke(0));
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (_closed) break;
                    _closed = true;
                    SteamNetworkingSockets.CloseConnection(_conn, 0, null, false);
                    _q.Enqueue(() => PeerDisconnected?.Invoke(0));
                    break;
            }
        }

        public void Send(int peer, byte[] data) { if (!_closed) SteamMsg.Send(_conn, data); }

        public void Disconnect(int peer)
        {
            if (_closed) return;
            _closed = true;
            SteamNetworkingSockets.CloseConnection(_conn, 0, "left", true);
            _q.Enqueue(() => PeerDisconnected?.Invoke(0));
        }

        public void Poll()
        {
            while (_q.Count > 0) _q.Dequeue()();
            if (!_closed) SteamMsg.Receive(_conn, d => Received?.Invoke(0, d));
        }

        public void Dispose()
        {
            if (!_closed) { _closed = true; SteamNetworkingSockets.CloseConnection(_conn, 0, "left", false); }
            _cb.Dispose();
        }
    }
}
