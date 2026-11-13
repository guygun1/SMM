using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using MPCore.Net;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>Replicated state of one player. Owner writes, everyone else reads.</summary>
    public class NetPlayerState : MonoBehaviour
    {
        [Synced] public Vector3 pos;
        [Synced] public Quaternion rot = Quaternion.identity;
        [Synced] public string playerName;
        [Synced] public Color color = Color.white;
        [Synced] public byte[] anim = new byte[0];   // the owner's Animator parameters, see PlayerAvatar
    }

    sealed class BepLog : MPCore.Sync.ILog
    {
        readonly BepInEx.Logging.ManualLogSource _l;
        public BepLog(BepInEx.Logging.ManualLogSource l) { _l = l; }
        /// <summary>A joined client also sends its log lines to the host, so the host's log tells both sides of the story.</summary>
        public static Action<string> Forward;
        public void Info(string m) { _l.LogInfo(m); Fwd("I " + m); }
        public void Warn(string m) { _l.LogWarning(m); Fwd("W " + m); }
        public void Error(string m) { _l.LogError(m); Fwd("E " + m); }
        static bool _in;
        static void Fwd(string m) { if (Forward == null || _in) return; _in = true; try { Forward(m); } catch { } finally { _in = false; } }
    }

    [BepInPlugin("com.guydevash.subnauticamp", "Subnautica MP", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin I;
        internal MPCore.Sync.ILog Log;

        ConfigEntry<string> _name, _clientOnly, _lastAddress, _color;
        ConfigEntry<int> _port;
        ConfigEntry<bool> _headless, _useSteam, _hostByDefault, _autoStartWorld, _dumpMenu, _downloadWorld;

        // what the panels read and write
        internal SyncSession S;
        internal HostService Host;
        internal bool HostNextGame, JoinPanelOpen, PausePanelOpen, ProfilePanelOpen;
        internal string ProfileName = "", ProfileColor = "";
        internal int JoinTab;
        internal string JoinText = "";
        string _joinStatus = "";
        /// <summary>The line shown to the player. Every change is also logged, so a log tells the whole story.</summary>
        internal string JoinStatus
        {
            get { return _joinStatus; }
            set
            {
                value = value ?? "";
                if (value == _joinStatus) return;
                _joinStatus = value;
                if (Log != null && value.Length > 0 && !value.StartsWith("Downloading the world")) Log.Info("status: " + value);
            }
        }
        internal Vector2 FriendScroll;
        internal List<SteamSupport.FriendInfo> SteamFriends = new List<SteamSupport.FriendInfo>();
        internal int Port { get { return _port.Value; } }
        internal bool DumpMenu { get { return _dumpMenu.Value; } }

        ConfigEntry<bool> _banner;
        readonly Queue<Action> _deferred = new Queue<Action>();
        internal bool ShowBanner { get { return _banner.Value; } }
        internal string PlayerName { get { return _name.Value; } }
        internal string PlayerColorHex { get { return _color.Value; } }
        internal Color PlayerColor { get { Color c; return ColorUtility.TryParseHtmlString("#" + _color.Value, out c) ? c : Color.white; } }

        /// <summary>Called by the name and colour dialog.</summary>
        internal void SetProfile(string name, string colorHex)
        {
            name = (name ?? "").Trim();
            if (name.Length > 0) _name.Value = name.Length > 24 ? name.Substring(0, 24) : name;
            if (!string.IsNullOrEmpty(colorHex)) _color.Value = colorHex;
            if (_localState != null) { _localState.playerName = _name.Value; _localState.color = PlayerColor; }
        }

        /// <summary>True when somebody else is in our world (host with a joined player, or any client). Pausing must not freeze time then.</summary>
        internal bool OthersConnected
        {
            get { return S != null && (S.IsHost ? S.PeerIds.Any() : S.Ready); }
        }

        GameObject _local;
        NetPlayerState _localState;
        readonly Dictionary<uint, GameObject> _proxies = new Dictionary<uint, GameObject>();
        bool _wasInWorld, _hostTried, _stopNext, _teleportDone;
        GameObject _hostProxy;
        float _worldSince = -1f;
        float _nextSteamCheck, _nextFriendRefresh, _nextAnim;
        bool _animWarned;

        void Awake()
        {
            I = this;
            Log = new BepLog(Logger);
            _port = Config.Bind("Network", "Port", 7777, "TCP port to host on and to join when no port is typed.");
            _useSteam = Config.Bind("Network", "UseSteam", true, "Also accept friends through Steam's relay (no port forwarding needed for them).");
            _headless = Config.Bind("Network", "Headless", false, "Host without a local player (dedicated host).");
            _hostByDefault = Config.Bind("Network", "HostNextWorldByDefault", false, "Start with the 'host multiplayer' box ticked.");
            _lastAddress = Config.Bind("Network", "LastAddress", "", "Last address typed in Join by IP.");
            _autoStartWorld = Config.Bind("Network", "EnterWorldAfterJoin", true, "After joining from the main menu and choosing your name and colour, start a new Survival world automatically so you land in the game. Uses a save slot; turn off to pick a save yourself.");
            _downloadWorld = Config.Bind("Network", "DownloadHostWorld", true, "A joining player receives the host's save and loads it, so both are in the same world. Uses a save slot on the joiner.");
            _name = Config.Bind("Player", "Name", Environment.UserName, "Name shown above your player.");
            _color = Config.Bind("Player", "Color", "3FA9F5", "Your colour (hex), shown on your name and body. Chosen in the dialog after joining.");
            HostSettings.Bind(Config);
            _clientOnly = Config.Bind("Compat", "ClientOnlyMods", "", "Comma separated plugin GUIDs that are cosmetic/local and need not match the host.");
            _dumpMenu = Config.Bind("Debug", "DumpMenuHierarchy", false, "Write the menu object tree to the log (helps if the menu buttons look wrong).");
            _banner = Config.Bind("Interface", "ShowHostingBanner", false, "Show a one-line overlay with the join address while hosting.");

            HostNextGame = _hostByDefault.Value;
            JoinText = _lastAddress.Value;
            UnityCodecs.Register();
            SyncRegistry.Register<NetPlayerState>();
            new Harmony("com.guydevash.subnauticamp").PatchAll(typeof(Plugin).Assembly);
        }

        void Start()
        {
            // Start() runs after every plugin's Awake(), so all mods' types exist by now.
            TypeDiscovery.RegisterAttributed(Log);
            TypeDiscovery.RegisterFromFile(System.IO.Path.Combine(Paths.ConfigPath, "SubnauticaMP.synced.txt"), Log);
        }

        void Update()
        {
            while (_deferred.Count > 0) { try { _deferred.Dequeue()(); } catch (Exception e) { Log.Warn("deferred: " + e.Message); } }
            FreezeGuard.Tick();
            WorldSync.Tick();
            if (_stopNext) { _stopNext = false; StopSession(); }

            if (!SteamSupport.Initialized && Time.unscaledTime > _nextSteamCheck)
            {
                _nextSteamCheck = Time.unscaledTime + 2f;
                SteamSupport.TryInit(m => Log.Info(m));
            }
            if (SteamSupport.PendingJoin != 0)
            {
                var id = SteamSupport.PendingJoin; SteamSupport.PendingJoin = 0;
                JoinSteam(id);
            }
            if (JoinPanelOpen && JoinTab == 1 && SteamSupport.Available && Time.unscaledTime > _nextFriendRefresh)
            {
                _nextFriendRefresh = Time.unscaledTime + 5f;
                SteamFriends = SteamSupport.Friends();
            }

            bool inWorld = Player.main != null;
            if (!inWorld && _wasInWorld && uGUI_MainMenu.main != null) { StopSession(); JoinStatus = ""; } // quit to menu
            _wasInWorld = inWorld;
            if (!inWorld) { _hostTried = false; _teleportDone = false; _worldSince = -1f; }
            else if (_worldSince < 0f) _worldSince = Time.unscaledTime;
            if (S == null && inWorld && HostNextGame && !_hostTried) { _hostTried = true; StartHosting(); }

            if (S == null) return;
            S.Tick();
            if (WorldTransfer.I != null) WorldTransfer.I.Tick();
            EntitySync.Tick();
            SyncKit.Tick();
            JoinerWorldFixups();
            DriveLocal();
            DriveProxies();
            AutoTeleportToHost();
        }

        // Worlds are not shared yet, so a joining player starts in their own save, far from the host.
        // Until the world state is synced, put the joiner next to the host so they can see each other.
        void AutoTeleportToHost()
        {
            if (WorldTransfer.I != null && WorldTransfer.I.Loaded) _teleportDone = true;   // the joiner loaded the host's save, so it already starts beside the host
            if (_teleportDone || S == null || S.IsHost || !S.Ready || Player.main == null) return;
            if (Time.unscaledTime - _worldSince < 8f) return;   // let the world finish loading first
            if (_hostProxy == null) return;
            if (TeleportToHost()) _teleportDone = true;
        }

        internal bool TeleportToHost()
        {
            if (_hostProxy == null || Player.main == null) { return false; }
            var st = _hostProxy.GetComponent<NetPlayerState>();
            if (st == null || st.pos == Vector3.zero) return false;
            try
            {
                Player.main.SetPosition(st.pos + Vector3.up * 1.5f);
                Log.Info("teleported next to the host at " + st.pos);
                try { ErrorMessage.AddMessage("Teleported to the host"); } catch { }
                return true;
            }
            catch (Exception e) { Log.Warn("teleport to host failed: " + e.Message); return false; }
        }

        void OnGUI() { Gui.Draw(this); }
        void OnDestroy() { StopSession(); }

        // ---------------------------------------------------------------- native options tab

        /// <summary>Run on the next frame. Widget callbacks must not rebuild the screen they are being called from.</summary>
        internal void Defer(Action a) { _deferred.Enqueue(a); }

        /// <summary>The tick box in the Multiplayer tab.</summary>
        internal void SetHosting(bool on)
        {
            HostNextGame = on;
            if (Player.main != null)
            {
                if (on) StartHosting();
                else if (Host != null) StopSession();
            }
            Defer(NativeOptions.Rebuild);
        }

        /// <summary>The pause menu's Multiplayer button: open the game's Options screen on our Multiplayer tab.</summary>
        internal void OpenMultiplayer() { StartCoroutine(OpenMultiplayerRoutine()); }

        System.Collections.IEnumerator OpenMultiplayerRoutine()
        {
            bool opened = false;
            if (!NativeOptions.Broken)
            {
                try { opened = OpenPauseOptions(); }
                catch (Exception e) { Log.Warn("could not open Options: " + e.Message); }
            }
            for (int i = 0; i < 4 && opened; i++)
            {
                yield return null;
                if (NativeOptions.SelectMpTab()) yield break;
            }
            PausePanelOpen = true;   // native tab unavailable: use the plain overlay instead
        }

        /// <summary>The pause menu's Options button is wired in the editor, so find the Options screen by its component.</summary>
        static bool OpenPauseOptions()
        {
            var im = IngameMenu.main;
            if (im == null) return false;
            var panel = im.GetComponentInChildren<uGUI_OptionsPanel>(true);
            if (panel == null) return false;
            var t = panel.transform;
            while (t.parent != null && t.parent != im.mainPanel.transform && t.parent != im.transform) t = t.parent;
            if (t.parent == null) return false;
            im.ChangeSubscreen(t.name);
            return true;
        }

        // ---------------------------------------------------------------- overlay panels (fallback)

        internal void OpenJoinPanel()
        {
            JoinPanelOpen = true;
            MainMenuInput.SetMenuClickable(false);
        }

        internal void OpenProfilePanel()
        {
            ProfileName = PlayerName; ProfileColor = PlayerColorHex;
            ProfilePanelOpen = true;
            MainMenuInput.SetMenuClickable(false);
        }

        /// <summary>The host could not share its world: start a fresh one instead (if allowed), otherwise just say so.</summary>
        internal void OnWorldTransferFailed(string why)
        {
            JoinStatus = "Could not get the host's world: " + why;
            if (_autoStartWorld.Value && S != null && !S.IsHost && MenuState.MainMenuVisible) { JoinStatus += " Starting a new world instead."; StartFreshWorld(); }
        }

        internal void CloseProfilePanel(bool save)
        {
            if (save) SetProfile(ProfileName, ProfileColor);
            Log.Info("profile chosen: " + PlayerName + " #" + PlayerColorHex);
            ProfilePanelOpen = false;
            MainMenuInput.SetMenuClickable(true);
            if (S != null && !S.IsHost && MenuState.MainMenuVisible)
            {
                if (_downloadWorld.Value && WorldTransfer.I != null) WorldTransfer.I.Request();
                else if (_autoStartWorld.Value) StartFreshWorld();
            }
        }

        internal void CloseJoinPanel()
        {
            JoinPanelOpen = false;
            MainMenuInput.SetMenuClickable(true);
            MainMenuUi.CloseDialog();
        }

        // ---------------------------------------------------------------- session control

        ModInfo[] CollectMods()
        {
            var clientOnly = new HashSet<string>(_clientOnly.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            return Chainloader.PluginInfos.Values
                .Select(p => new ModInfo { Id = p.Metadata.GUID, Version = p.Metadata.Version.ToString(), ClientOnly = clientOnly.Contains(p.Metadata.GUID) })
                .ToArray();
        }

        void Configure(SyncSession s)
        {
            s.Log = Log;
            s.Name = _name.Value;
            s.Mods = CollectMods();
            s.RequiredMods.Add(Info.Metadata.GUID);   // the multiplayer mod itself must always match
            s.IsAlive = o => !(o is UnityEngine.Object u) || u != null; // Unity's fake-null
            s.SpawnReceived += OnSpawnReceived;
            s.Despawned += OnDespawned;
            s.PeerJoined += (id, n) => Log.Info(n + " joined");
            s.PeerLeft += id => Log.Info("peer " + id + " left");
            WorldSync.Attach(s);
            WorldTransfer.Attach(this, s);
            EntitySync.Attach(s);
            SyncKit.Attach(s);
            s.RegisterMessage("dmg", (from, r) => PvP.OnDamage(from, r));
            Application.runInBackground = true;   // an unfocused window must keep sending and receiving
            s.RegisterMessage("log", (from, r) => Logger.LogInfo("[peer " + from + "] " + r.Str()));
            if (!s.IsHost)
            {
                int sentThisSecond = 0; float second = 0f;
                BepLog.Forward = m =>
                {
                    if (S == null || S.IsHost || !S.Ready) return;
                    if (Time.unscaledTime - second > 1f) { second = Time.unscaledTime; sentThisSecond = 0; }
                    if (++sentThisSecond > 30) return;
                    var w = new MPCore.IO.ByteWriter(); w.Str(m);
                    S.SendMessage("log", w, 0);
                };
            }
        }

        internal void StartHosting()
        {
            if (S != null) return;
            try
            {
                Host = HostService.Start(_port.Value, _useSteam.Value, Log);
                S = Host.Session;
                Configure(S);
                JoinStatus = "";
                Log.Info("hosting: tcp=" + Host.TcpOk + " steam=" + Host.SteamOk);
                SpawnLocalPlayer();
            }
            catch (Exception e)
            {
                Log.Error("host failed: " + e.Message);
                JoinStatus = "Could not start hosting: " + e.Message;
                if (Host != null) Host.Dispose();
                Host = null; S = null;
            }
        }

        internal void JoinFromText()
        {
            Log.Info("join requested: '" + JoinText + "'");
            var t = JoinTarget.Parse(JoinText);
            if (t.Kind == JoinKind.Invalid) { JoinStatus = "That address does not look right (" + t.Error + ")."; return; }
            _lastAddress.Value = JoinText.Trim();
            if (t.Kind == JoinKind.Steam) { JoinSteam(t.SteamId); return; }
            StartClient(() => new TcpClientTransport(t.Host, t.Port), "Connecting to " + t.Host + ":" + t.Port + "...");
        }

        internal void JoinSteam(ulong steamId)
        {
            if (!SteamSupport.Available) { JoinStatus = "Steam is not running, so Steam join is not available."; return; }
            StartClient(() => new SteamClientTransport(steamId), "Connecting through Steam...");
        }

        Func<ITransport> _lastMake; string _lastStatus; bool _acceptModsNext;

        /// <summary>The player saw the list of different mods and chose to join anyway.</summary>
        internal void JoinAnyway()
        {
            if (_lastMake == null) return;
            _acceptModsNext = true;
            StartClient(_lastMake, _lastStatus);
        }

        void StartClient(Func<ITransport> make, string status)
        {
            StopSession();
            _lastMake = make; _lastStatus = status;
            try
            {
                S = SyncSession.CreateClient(make());
                Configure(S);
                S.AcceptModDifferences = _acceptModsNext;
                _acceptModsNext = false;
                S.ModsDiffer += diffs =>
                {
                    JoinStatus = "The host has different mods (" + diffs.Count + "). Join anyway or cancel.";
                    ModCheck.Show(diffs);
                    _stopNext = true;
                };
                JoinStatus = status;
                S.Joined += () =>
                {
                    try
                    {
                        Log.Info("connected to the host");
                        CloseJoinPanel();
                        SpawnLocalPlayer();
                        JoinStatus = "Connected. Choose your player, then the game starts.";
                        if (MenuState.MainMenuVisible) { OpenProfilePanel(); Log.Info("opened the name and colour window"); }
                    }
                    catch (Exception e) { Log.Error("after join: " + e); }
                };
                S.Rejected += why => { JoinStatus = "The host refused you: " + why; _stopNext = true; };
                S.Disconnected += () =>
                {
                    if (!JoinStatus.StartsWith("The host refused") && !ModCheck.Visible && !JoinStatus.StartsWith("The host has different mods"))
                        JoinStatus = JoinStatus.StartsWith("Connecting")
                            ? "Could not connect. Check the address, that the host is running, and that the port is open."
                            : "Disconnected from the host.";
                    _stopNext = true;
                };
            }
            catch (Exception e)
            {
                Log.Error("join failed: " + e.Message);
                JoinStatus = "Could not start the connection: " + e.Message;
                S = null;
            }
        }

        internal void StartFreshWorld()
        {
            try { Log.Info("starting a new Survival world"); uGUI_MainMenu.main.OnButtonSurvival(); }
            catch (Exception e) { Log.Warn("could not start a world automatically: " + e.Message); }
        }

        internal void StopSession()
        {
            try
            {
                if (Host != null) Host.Dispose();
                else if (S != null) S.Dispose();
            }
            catch (Exception e) { Log.Warn("stop: " + e.Message); }
            WorldSync.Detach(); WorldTransfer.Detach(); EntitySync.Detach(); SyncKit.Detach(); BepLog.Forward = null;
            Host = null; S = null;
            foreach (var p in _proxies.Values) if (p) Destroy(p);
            _proxies.Clear();
            if (_local) Destroy(_local);
            _local = null; _localState = null;
            _hostProxy = null; _teleportDone = false; _inventoryCleared = false;
        }

        // ---------------------------------------------------------------- players

        void SpawnLocalPlayer()
        {
            if (S == null || (S.IsHost && _headless.Value)) return; // a dedicated host has no player of its own
            var o = S.Spawn("player", Encoding.UTF8.GetBytes(_name.Value));
            _local = new GameObject("MP_LocalPlayerState");
            DontDestroyOnLoad(_local);
            _localState = _local.AddComponent<NetPlayerState>();
            _localState.playerName = _name.Value;
            _localState.color = PlayerColor;
            AutoBind.Bind(S, o, _local);
            o.UserData = _local;
        }

        void DriveLocal()
        {
            if (_localState == null) return;
            var p = Player.main;
            if (p == null) return; // main menu / loading
            // the pose of the animated body (what remote players copy), so the copy lands exactly where the body is
            var anim = PlayerAvatar.LocalAnimator();
            var t = anim != null ? anim.transform : p.transform;
            // dead zones keep idle players from generating traffic
            if ((t.position - _localState.pos).sqrMagnitude > 1e-4f) _localState.pos = t.position;
            if (Quaternion.Angle(t.rotation, _localState.rot) > 0.25f) _localState.rot = t.rotation;

            if (Time.unscaledTime >= _nextAnim)
            {
                _nextAnim = Time.unscaledTime + 0.066f;   // ~15 updates per second
                try
                {
                    var b = PlayerAvatar.Encode(PlayerAvatar.LocalAnimator());
                    if (b != null && !PlayerAvatar.Same(b, _localState.anim)) _localState.anim = b;
                }
                catch (Exception e) { if (!_animWarned) { _animWarned = true; Log.Warn("animation sync: " + e.Message); } }
            }
        }

        void OnSpawnReceived(NetObject o)
        {
            if (o.Prefab != "player") return; // other prefabs are the job of other adapters / future milestones
            string nm = o.SpawnData != null ? Encoding.UTF8.GetString(o.SpawnData) : "?";
            var go = new GameObject("MP_Player_" + nm);   // root: position and rotation come from the replicated state
            DontDestroyOnLoad(go);

            var label = new GameObject("label");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(0, 0.9f, 0);
            var tm = label.AddComponent<TextMesh>();
            tm.text = nm; tm.characterSize = 0.1f; tm.fontSize = 48; tm.anchor = TextAnchor.LowerCenter;
            tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.GetComponent<MeshRenderer>().material = tm.font.material;

            var st0 = go.AddComponent<NetPlayerState>();
            var ra = go.AddComponent<RemoteAvatar>(); ra.state = st0; ra.OwnerPeer = o.Owner;   // the body is attached once a world is loaded
            AutoBind.Bind(S, o, go);
            _proxies[o.Id] = go;
            o.UserData = go;
            if (S != null && !S.IsHost && o.Owner == 0) _hostProxy = go;
        }

        void OnDespawned(NetObject o)
        {
            GameObject go;
            if (_proxies.TryGetValue(o.Id, out go)) { if (go) Destroy(go); _proxies.Remove(o.Id); }
        }

        float _nextPosLog;
        bool _inventoryCleared;

        internal IEnumerable<Vector3> RemotePlayerPositions()
        {
            foreach (var g in _proxies.Values) if (g) yield return g.transform.position;
        }

        internal Vector3? HostPosition()
        {
            if (_hostProxy == null) return null;
            return _hostProxy.transform.position;
        }

        /// <summary>The joiner loaded the host's save, which includes the host's inventory. Start the joiner empty
        /// (like a new player), otherwise both carry copies of the same items with the same ids.</summary>
        void JoinerWorldFixups()
        {
            if (_inventoryCleared || S == null || S.IsHost || WorldTransfer.I == null || !WorldTransfer.I.Loaded) return;
            if (Player.main == null || Inventory.main == null || Time.unscaledTime - _worldSince < 3f) return;
            _inventoryCleared = true;
            try
            {
                Inventory.main.container.Clear(false);
                Inventory.main.equipment.ClearItems();
                Log.Info("joiner: cleared the copied inventory of the host");
            }
            catch (Exception e) { Log.Warn("joiner: could not clear inventory: " + e.Message); }
        }

        void DriveProxies()
        {
            if (Time.unscaledTime > _nextPosLog)
            {
                _nextPosLog = Time.unscaledTime + 5f;
                var me = Player.main != null ? Player.main.transform.position : Vector3.zero;
                foreach (var g in _proxies.Values)
                {
                    if (!g) continue;
                    var s0 = g.GetComponent<NetPlayerState>();
                    var ra0 = g.GetComponent<RemoteAvatar>();
                    if (s0 != null) Log.Info("player " + s0.playerName + " at " + s0.pos + " (" + (int)Vector3.Distance(me, s0.pos) + " m from me), body=" + (ra0 != null && ra0.body != null ? ra0.body.name : "none"));
                }
                if (_localState != null && Player.main != null) Log.Info("me at " + Player.main.transform.position + ", sent pos " + _localState.pos);
            }
            float k = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime); // unscaled: the pause menu freezes game time
            var cam = Camera.main;
            foreach (var go in _proxies.Values)
            {
                if (!go) continue;
                var st = go.GetComponent<NetPlayerState>();
                if (st == null) continue;
                var tr = go.transform;
                tr.position = (tr.position - st.pos).sqrMagnitude > 400f ? st.pos : Vector3.Lerp(tr.position, st.pos, k); // snap on teleport
                tr.rotation = Quaternion.Slerp(tr.rotation, st.rot, k);
                var ra = go.GetComponent<RemoteAvatar>();
                if (ra != null) ra.Drive();
                var label = tr.Find("label");
                if (label != null)
                {
                    var tm = label.GetComponent<TextMesh>();
                    if (tm != null)
                    {
                        if (!string.IsNullOrEmpty(st.playerName) && tm.text != st.playerName) tm.text = st.playerName;
                        tm.color = st.color;
                    }
                    if (cam != null) label.rotation = cam.transform.rotation; // billboard
                }
            }
        }
    }
}
