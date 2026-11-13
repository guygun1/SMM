using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using MPCore.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SubnauticaMP
{
    /// <summary>
    /// Adds a "Multiplayer" tab to Subnautica's own Options screen, built from the game's own widget calls
    /// (AddTab, AddToggleOption, AddButton...). The game rebuilds its tabs every time the screen opens, so this runs
    /// each time and always shows current state. Works from the main menu and from the pause menu.
    /// If anything here throws, Broken is set and the plugin falls back to the plain overlay panels.
    /// </summary>
    static class NativeOptions
    {
        public static int MpTab = -1;
        public static uGUI_OptionsPanel Panel;
        public static bool Broken;

        static readonly MethodInfo SelectTabM = AccessTools.Method(typeof(uGUI_TabbedControlsPanel), "SelectTab");
        static readonly MethodInfo AddTabsM = AccessTools.Method(typeof(uGUI_OptionsPanel), "AddTabs");
        static readonly MethodInfo GetParentM = AccessTools.Method(typeof(uGUI_TabbedControlsPanel), "GetParent");

        public static bool PanelAlive { get { return Panel != null && Panel.gameObject.activeInHierarchy && MpTab >= 0; } }

        // ------------------------------------------------------------------ build

        static readonly Color Good = new Color(0.45f, 1f, 0.55f), Warn = new Color(1f, 0.8f, 0.3f), Bad = new Color(1f, 0.55f, 0.4f), Soft = new Color(0.72f, 0.82f, 0.92f);

        public static void Build(uGUI_OptionsPanel p)
        {
            var pl = Plugin.I;
            if (pl == null) return;
            Panel = p;
            if (Player.main == null) { MpTab = -1; return; }   // on the main menu, hosting and joining live on the Play screen
            int tab = p.AddTab("Multiplayer");
            MpTab = tab;
            bool hosting = pl.Host != null;
            bool client = pl.S != null && !hosting;

            // ---- status at a glance
            p.AddHeading(tab, "Status");
            if (hosting)
            {
                int n = pl.S.PeerIds.Count();
                Info(p, tab, n == 0 ? "Hosting. Nobody has joined yet." : "Hosting with " + n + (n == 1 ? " other player." : " other players."), Good, 1f);
            }
            else if (client) Info(p, tab, "Connected to a host (you are player " + pl.S.LocalPeer + ").", Good, 1f);
            else Info(p, tab, "Not hosting. This world is single player.", Soft, 1f);
            if (!string.IsNullOrEmpty(pl.JoinStatus) && !client && !hosting) Info(p, tab, pl.JoinStatus, Warn);

            if (!client)
                p.AddToggleOption(tab, "Host this world for friends", hosting, v => pl.SetHosting(v));

            if (hosting) BuildHosting(p, tab, pl);
            else if (client)
            {
                p.AddButton(tab, "Leave game", () => { pl.StopSession(); pl.Defer(Rebuild); });
            }

            // ---- game rules: the host decides, everybody sees
            if (hosting || client) BuildRules(p, tab, hosting);

            // ---- players
            if (pl.S != null)
            {
                p.AddHeading(tab, "Players");
                Color me = pl.PlayerColor;
                Info(p, tab, pl.PlayerName + "  (you)", me, 1f);
                foreach (var id in pl.S.PeerIds)
                {
                    var nm = pl.S.PeerName(id);
                    Info(p, tab, string.IsNullOrEmpty(nm) ? "Player " + id : nm, Color.white, 1f);
                }
                if (pl.S.PeerIds.Count() == 0 && hosting) Info(p, tab, "Friends appear here when they join.", Soft);
            }

            // ---- your player
            p.AddHeading(tab, "Your player");
            p.AddButton(tab, "Unstuck", () => Features.Unstuck.Run());
            Info(p, tab, "Name: " + pl.PlayerName, pl.PlayerColor, 1f);
            p.AddButton(tab, "Change name and colour...", () => pl.Defer(pl.OpenProfilePanel));

            // ---- settings
            p.AddHeading(tab, "Settings");
            Toggle(p, tab, pl, "Show hosting banner on screen", "_banner");
            Toggle(p, tab, pl, "Allow Steam join when I host", "_useSteam");
            Info(p, tab, "Steam join works behind any router. It applies the next time hosting starts.", Soft);
            Toggle(p, tab, pl, "Download the host's world when I join", "_downloadWorld");
            Toggle(p, tab, pl, "Start a world automatically after joining", "_autoStartWorld");
            Info(p, tab, "Used when you join from the main menu. Both options use a save slot. Port " + pl.Port + " can be changed in BepInEx/config (Network/Port).", Soft);

            if (DebugLog.Entry != null)
            {
                var dbg = DebugLog.Entry;
                p.AddToggleOption(tab, "Advanced debug log (for bug reports)", dbg.Value, v => dbg.Value = v);
                Info(p, tab, "Writes a very detailed log (sounds, messages, vehicles, Cyclops) to BepInEx/LogOutput.log. Turn it on, reproduce the problem, then send the log. Ctrl+F9 also toggles it.", Soft);
            }

            if (pl.S != null)
            {
                int d = 0; foreach (var c in pl.S.DesyncCounts.Values) d += c;
                if (d > 0) Info(p, tab, "Sync problems logged: " + d + ". The BepInEx log names the mod responsible.", Warn);
            }
        }

        static void BuildRules(uGUI_OptionsPanel p, int tab, bool hosting)
        {
            p.AddHeading(tab, "Game rules");
            if (hosting)
            {
                foreach (var kv in HostSettings.Rules())
                {
                    var e = kv.Value;
                    p.AddToggleOption(tab, kv.Key, e.Value, v => e.Value = v);
                }
                Info(p, tab, "On: everyone shares it. Off: each player keeps their own. Changes reach players within a second.", Soft);
            }
            else
            {
                Info(p, tab, "The host decides these: " + HostSettings.Describe() + ".", Soft);
            }
        }

        static void BuildHosting(uGUI_OptionsPanel p, int tab, Plugin pl)
        {
            var host = pl.Host;
            var r = host.Report;

            // ---- how friends reach you
            p.AddHeading(tab, "Join address");
            if (r == null || r.JoinAddress == null)
            {
                if (host.TcpOk && r == null) Info(p, tab, "Checking your connection...", Warn);
                else if (host.TcpOk) Info(p, tab, "No address works from outside your network right now (see Connection below).", Warn);
                else Info(p, tab, "Address join is off: port " + host.Port + " could not be opened (" + host.TcpError + ").", Bad);
            }
            else
            {
                Info(p, tab, "Friends type this to join:", Soft);
                Info(p, tab, r.JoinAddress, Color.white, 1.15f);
                var addr = r.JoinAddress;
                p.AddButton(tab, "Copy join address", () => Copy(addr, "Join address copied"));
            }
            if (r != null && r.LanAddress != null)
            {
                var lan = r.LanAddress;
                Info(p, tab, "Same network (LAN): " + lan, Soft);
                p.AddButton(tab, "Copy LAN address", () => Copy(lan, "LAN address copied"));
            }
            if (host.SteamOk)
            {
                Info(p, tab, "Steam: friends can click Join in their Steam friends list, or use your Steam address.", Soft);
                p.AddButton(tab, "Invite Steam friends", () => SteamSupport.InviteFriends());
                p.AddButton(tab, "Copy Steam address", () => Copy("steam:" + SteamSupport.MyId, "Steam address copied"));
            }

            // ---- port forwarding advice
            if (r != null)
            {
                p.AddHeading(tab, "Connection");
                string head;
                switch (r.State)
                {
                    case Reach.OpenViaUpnp: head = "Port " + host.Port + " was opened automatically. No port forwarding needed."; break;
                    case Reach.NeedsPortForward: head = "Port forwarding needed for address join."; break;
                    case Reach.Cgnat: head = "Your provider shares one address between customers. Port forwarding cannot work."; break;
                    default: head = "Checking..."; break;
                }
                Info(p, tab, head, ColorFor(r.State), 1f);
                if (!string.IsNullOrEmpty(r.Message) && r.Message != head) Info(p, tab, r.Message, ColorFor(r.State));
                if (r.State == Reach.NeedsPortForward && r.LocalIp != null)
                    Info(p, tab, "In your router, forward TCP port " + host.Port + " to " + r.LocalIp + ".", Warn);
                if (r.State == Reach.Cgnat) Info(p, tab, "Use Steam join, a VPN, or ask your provider for a public address.", Bad);
                p.AddButton(tab, "Check again", () => pl.Defer(Rebuild));
            }
        }

        // ------------------------------------------------------------------ rows the game has no widget for

        /// <summary>A line of plain text (wrapped), built from the game's heading row but smaller and tinted.</summary>
        static void Info(uGUI_OptionsPanel p, int tab, string text, Color c, float scale = 0.8f)
        {
            foreach (var line in Wrap(text, scale >= 1f ? 56 : 70))
            {
                var go = p.AddHeading(tab, line);
                try
                {
                    var t = go != null ? go.GetComponentInChildren<TMP_Text>(true) : null;
                    if (t == null) continue;
                    t.fontSize = t.fontSize * (scale >= 1f ? 0.9f * scale : scale);
                    t.fontStyle = FontStyles.Normal;
                    t.color = c;
                }
                catch { }
            }
        }

        /// <summary>A switch bound to a bool BepInEx config entry of the plugin (found by field name, so Plugin.cs needs no change).</summary>
        static void Toggle(uGUI_OptionsPanel p, int tab, Plugin pl, string label, string field)
        {
            var f = AccessTools.Field(typeof(Plugin), field);
            var e = f != null ? f.GetValue(pl) as ConfigEntry<bool> : null;
            if (e == null) return;
            p.AddToggleOption(tab, label, e.Value, v => e.Value = v);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Text component of the row that was added last in this tab (the widget calls do not hand it back).</summary>
        static TMP_Text LastText(uGUI_OptionsPanel p, int tab)
        {
            try
            {
                var parent = GetParentM != null ? GetParentM.Invoke(p, new object[] { tab }) as Transform : null;
                if (parent == null || parent.childCount == 0) return null;
                return parent.GetChild(parent.childCount - 1).GetComponentInChildren<TMP_Text>(true);
            }
            catch { return null; }
        }

        static void Tint(uGUI_OptionsPanel p, int tab, Color c)
        {
            var t = LastText(p, tab);
            if (t != null) t.color = c;
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

        public static IEnumerable<string> Wrap(string s, int max = 58)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            var line = "";
            foreach (var w in s.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + w.Length > max) { yield return line; line = w; }
                else line = line.Length == 0 ? w : line + " " + w;
            }
            if (line.Length > 0) yield return line;
        }

        public static void Copy(string text, string toast)
        {
            GUIUtility.systemCopyBuffer = text;
            try { ErrorMessage.AddMessage(toast); } catch { }
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>Rebuilds the tab list (as if the screen were reopened) and stays on our tab.</summary>
        public static void Rebuild()
        {
            if (!PanelAlive || AddTabsM == null) return;
            try
            {
                Panel.RemoveTabs();
                AddTabsM.Invoke(Panel, null);   // our patch adds the tab again
                SelectMpTab();
            }
            catch (Exception e) { Plugin.I.Log.Warn("options rebuild: " + e.Message); }
        }

        public static bool SelectMpTab()
        {
            if (!PanelAlive || SelectTabM == null) return false;
            try
            {
                var ps = SelectTabM.GetParameters();
                var args = new object[ps.Length];
                if (ps.Length > 0) args[0] = MpTab;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                SelectTabM.Invoke(Panel, args);
                return true;
            }
            catch (Exception e) { Plugin.I.Log.Warn("select tab: " + e.Message); return false; }
        }
    }

    [HarmonyPatch(typeof(uGUI_OptionsPanel), "AddTabs")]
    static class P_OptionsAddTabs
    {
        static void Postfix(uGUI_OptionsPanel __instance)
        {
            try { NativeOptions.Build(__instance); }
            catch (Exception e)
            {
                NativeOptions.Broken = true;
                NativeOptions.MpTab = -1;
                if (Plugin.I != null) Plugin.I.Log.Error("native Multiplayer tab failed, using overlay panels instead: " + e);
            }
        }
    }
}
