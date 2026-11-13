using System.Collections.Generic;
using System.Linq;
using MPCore.Net;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// The panels. Drawn with Unity's built-in GUI so they cannot break when the game's own UI changes.
    /// The entry buttons in the menus are real (cloned) game buttons; see MenuPatches.
    /// </summary>
    static class Gui
    {
        static Rect _join = new Rect(0, 0, 520, 10);
        static Rect _profile = new Rect(0, 0, 460, 10);
        static Rect _pause = new Rect(0, 0, 400, 10);
        static GUIStyle _wrap, _title;
        static float _copiedAt = -10f;
        static bool _wasProfile;

        static void Styles()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        }

        static void Copy(string text)
        {
            GUIUtility.systemCopyBuffer = text;
            _copiedAt = Time.realtimeSinceStartup;
        }

        public static void Draw(Plugin p)
        {
            Styles();
            float s = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1));
            float W = Screen.width / s, H = Screen.height / s;

            // Join / profile dialogs: the game-styled uGUI versions (UiDialogs). The plain IMGUI windows below are the fallback.
            bool main = MenuState.MainMenuVisible;
            if (_wasProfile && !p.ProfilePanelOpen && !main && Event.current.type == EventType.Layout) p.Defer(NativeOptions.Rebuild);   // the Options tab shows the new name
            if (Event.current.type == EventType.Layout) _wasProfile = p.ProfilePanelOpen;
            string want = UiDialogs.MessageShowing && !UiDialogs.Failed ? "msg" : p.ProfilePanelOpen ? "profile" : (p.JoinPanelOpen && main ? "join" : null);
            bool native = !UiDialogs.Failed;
            if (native && Event.current.type == EventType.Layout) UiDialogs.Sync(p, want);
            native = !UiDialogs.Failed;
            if (native && want != null) MainMenuUi.DrawFields(s);   // the text boxes sit on top of the uGUI backdrops

            if (main)
            {
                if (!p.JoinPanelOpen && !p.ProfilePanelOpen && !string.IsNullOrEmpty(p.JoinStatus)) GUI.Label(new Rect(14, H - 90, Mathf.Min(1100, W - 28), 80), p.JoinStatus, _wrap);
                if (native) { /* dialogs are uGUI */ }
                else if (p.ProfilePanelOpen)
                {
                    if (_profile.x == 0 && _profile.y == 0) _profile.position = new Vector2((W - _profile.width) / 2, H * 0.22f);
                    _profile = GUILayout.Window(7103, _profile, id => ProfileWindow(p), "Choose your player");
                }
                else if (p.JoinPanelOpen)
                {
                    if (_join.x == 0 && _join.y == 0) _join.position = new Vector2((W - _join.width) / 2, H * 0.18f);
                    _join = GUILayout.Window(7101, _join, id => JoinWindow(p), "Multiplayer");
                }
                if (!p.JoinPanelOpen && !p.ProfilePanelOpen && MainMenuUi.Broken)   // normally the buttons on the game's Play screen do this
                {
                    if (GUI.Button(new Rect(14, H - 106, 220, 30), "Multiplayer: join...")) p.OpenJoinPanel();
                    p.HostNextGame = GUI.Toggle(new Rect(14, H - 40, 460, 28), p.HostNextGame, "  Host multiplayer for the next world I start or load");
                    if (!string.IsNullOrEmpty(p.JoinStatus)) GUI.Label(new Rect(14, H - 70, 900, 28), p.JoinStatus);
                }
            }
            else if (Player.main != null)
            {
                if (DebugLog.On && SyncKit.InSession) { try { DebugHud(p, W); } catch { } }
                if (p.ProfilePanelOpen && !native)
                {
                    if (_profile.x == 0 && _profile.y == 0) _profile.position = new Vector2((W - _profile.width) / 2, H * 0.22f);
                    _profile = GUILayout.Window(7103, _profile, id => ProfileWindow(p), "Choose your player");
                }
                else if (p.ProfilePanelOpen) { /* uGUI dialog */ }
                else if (p.PausePanelOpen && MenuState.PauseOpen)
                {
                    _pause.position = new Vector2(W - _pause.width - 24, H * 0.12f);
                    _pause = GUILayout.Window(7102, _pause, id => PauseWindow(p), "Multiplayer");
                }
                else if (p.ShowBanner && p.Host != null && !MenuState.PauseOpen)
                {
                    var addr = p.Host.InternetAddress;
                    GUI.Label(new Rect(14, 10, 900, 26), "Hosting: " + (p.S != null ? p.S.PeerIds.Count() : 0) + " player(s)" +
                        (addr != null ? "   Join at " + addr : p.Host.SteamOk ? "   (Steam join)" : "") + "   [Pause menu > Multiplayer for details]");
                }
            }
        }

        static GUIStyle _hud;

        /// <summary>Advanced debug only: a small block of session facts in the top right corner.</summary>
        static void DebugHud(Plugin p, float W)
        {
            var s = p.S;
            if (s == null) return;
            if (_hud == null) _hud = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontSize = 15, richText = false };
            var ids = new HashSet<int>(s.PeerIds); ids.Add(0); if (s.LocalPeer >= 0) ids.Add(s.LocalPeer);
            var sb = new System.Text.StringBuilder();
            sb.Append("MP debug: ").Append(s.IsHost ? "host" : "joined").Append(", ").Append(ids.Count).Append(ids.Count == 1 ? " player" : " players").Append('\n');
            foreach (var id in ids.OrderBy(x => x))
            {
                string n = id == s.LocalPeer ? (s.Name ?? "me") + " (you)" : s.PeerName(id);
                if (string.IsNullOrEmpty(n)) n = id == 0 ? "host" : "peer " + id;
                sb.Append("  ").Append(n).Append('\n');
            }
            sb.Append("sync delay ").Append((int)(NetInterp.Delay * 1000f)).Append(" ms, ").Append((int)(1f / Mathf.Max(0.001f, Time.unscaledDeltaTime))).Append(" fps\n");
            if (!s.IsHost) sb.Append("last from host ").Append(s.SilentMs).Append(" ms ago\n");
            sb.Append("sounds skipped ").Append(SoundWatch.Skipped).Append(", suppressed ").Append(SoundWatch.Suppressed);
            var r = new Rect(W - 520, 10, 510, 200);
            GUI.color = new Color(0f, 0f, 0f, 0.8f); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), sb.ToString(), _hud);
            GUI.color = new Color(0.75f, 1f, 0.8f, 1f); GUI.Label(r, sb.ToString(), _hud);
            GUI.color = Color.white;
        }

        static readonly string[] Palette = { "F28C28", "E5484D", "F472B6", "A78BFA", "3FA9F5", "22D3EE", "4ADE80", "FACC15", "F5F5F5", "9CA3AF" };

        static void ProfileWindow(Plugin p)
        {
            GUILayout.Label("Name");
            p.ProfileName = GUILayout.TextField(p.ProfileName ?? "", 24);
            GUILayout.Space(8);
            GUILayout.Label("Colour");
            GUILayout.BeginHorizontal();
            var old = GUI.backgroundColor;
            foreach (var hex in Palette)
            {
                Color c; ColorUtility.TryParseHtmlString("#" + hex, out c);
                GUI.backgroundColor = c;
                bool sel = string.Equals(hex, p.ProfileColor, System.StringComparison.OrdinalIgnoreCase);
                if (GUILayout.Button(sel ? "X" : " ", GUILayout.Height(34))) p.ProfileColor = hex;
            }
            GUI.backgroundColor = old;
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            if (GUILayout.Button("Done", GUILayout.Height(32))) p.CloseProfilePanel(true);
            GUI.DragWindow();
        }

        // ------------------------------------------------------------------ main menu: join / host

        static void JoinWindow(Plugin p)
        {
            p.HostNextGame = GUILayout.Toggle(p.HostNextGame, "  Host multiplayer for the next world I start or load");
            if (p.HostNextGame)
                GUILayout.Label("When the world loads, friends can join you. Port " + p.Port + " is opened on your router automatically if it allows it, and you will be told if you still need to forward it.", _wrap);
            GUILayout.Space(10);

            GUILayout.Label("Join a friend's world", _title);
            p.JoinTab = GUILayout.Toolbar(p.JoinTab, new[] { "Join by IP", "Steam friends" });
            GUILayout.Space(6);

            if (p.JoinTab == 0)
            {
                GUILayout.Label("Address (ip, ip:port, or steam:id)");
                GUILayout.BeginHorizontal();
                p.JoinText = GUILayout.TextField(p.JoinText ?? "", 80, GUILayout.MinWidth(300));
                if (GUILayout.Button("Join", GUILayout.Width(90))) p.JoinFromText();
                GUILayout.EndHorizontal();
            }
            else
            {
                if (!SteamSupport.Available)
                    GUILayout.Label("Steam is not running, or this is not the Steam version of the game. Use Join by IP.", _wrap);
                else
                {
                    var friends = p.SteamFriends;
                    if (friends.Count == 0) GUILayout.Label("No friends found yet...");
                    p.FriendScroll = GUILayout.BeginScrollView(p.FriendScroll, GUILayout.Height(170));
                    foreach (var f in friends)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(f.Name + (f.Hosting ? "   (hosting)" : ""), GUILayout.Width(300));
                        GUI.enabled = f.Hosting;
                        if (GUILayout.Button("Join", GUILayout.Width(90))) p.JoinSteam(f.Id);
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                    GUILayout.Label("Friends show as hosting while they have a multiplayer world open. Steam join needs no port forwarding.", _wrap);
                }
            }

            if (!string.IsNullOrEmpty(p.JoinStatus)) { GUILayout.Space(6); GUILayout.Label(p.JoinStatus, _wrap); }
            GUILayout.Space(8);
            if (GUILayout.Button("Close")) p.CloseJoinPanel();
            GUI.DragWindow();
        }

        // ------------------------------------------------------------------ in game: host / address / port forward advice

        static void PauseWindow(Plugin p)
        {
            var s = p.S;
            if (s == null)
            {
                GUILayout.Label("Let friends join this world.", _wrap);
                if (GUILayout.Button("Start hosting")) p.StartHosting();
                if (!string.IsNullOrEmpty(p.JoinStatus)) GUILayout.Label(p.JoinStatus, _wrap);
            }
            else if (p.Host != null)
            {
                GUILayout.Label("Hosting: " + s.PeerIds.Count() + " player(s) connected");
                GUILayout.Label("Ways to join: " + (p.Host.TcpOk ? "IP address" : "") + (p.Host.TcpOk && p.Host.SteamOk ? " and " : "") + (p.Host.SteamOk ? "Steam" : ""));
                GUILayout.Space(6);

                var r = p.Host.Report;
                if (r != null)
                {
                    var old = GUI.contentColor;
                    GUI.contentColor = ColorFor(r.State);
                    GUILayout.Label(r.Message, _wrap);
                    GUI.contentColor = old;

                    if (r.JoinAddress != null)
                    {
                        GUILayout.Label("Friends type this to join:");
                        GUILayout.TextField(r.JoinAddress);
                        if (GUILayout.Button("Copy join address")) Copy(r.JoinAddress);
                    }
                    if (r.LanAddress != null && GUILayout.Button("Copy LAN address (" + r.LanAddress + ")")) Copy(r.LanAddress);
                }
                if (p.Host.SteamOk)
                {
                    GUILayout.Space(6);
                    if (GUILayout.Button("Invite Steam friends")) SteamSupport.InviteFriends();
                    GUILayout.Label("Or copy your Steam id for Join by IP:");
                    var sid = "steam:" + SteamSupport.MyId;
                    GUILayout.TextField(sid);
                    if (GUILayout.Button("Copy Steam id")) Copy(sid);
                }
                if (Time.realtimeSinceStartup - _copiedAt < 2f) GUILayout.Label("Copied to clipboard.");
                GUILayout.Space(8);
                if (GUILayout.Button("Stop hosting")) p.StopSession();
            }
            else
            {
                GUILayout.Label("Connected to a host as peer " + s.LocalPeer + ".");
                if (GUILayout.Button("Leave")) p.StopSession();
            }

            if (s != null)
            {
                int d = 0; foreach (var c in s.DesyncCounts.Values) d += c;
                if (d > 0) GUILayout.Label("Sync problems logged: " + d + " (see BepInEx log for the mod responsible).", _wrap);
            }
        }

        static Color ColorFor(Reach r)
        {
            switch (r)
            {
                case Reach.OpenViaUpnp: return new Color(0.45f, 1f, 0.5f);
                case Reach.NeedsPortForward: return new Color(1f, 0.8f, 0.3f);
                case Reach.Cgnat: return new Color(1f, 0.55f, 0.4f);
                default: return Color.white;
            }
        }
    }
}
