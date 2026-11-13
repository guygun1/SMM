using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace MPCore.Net
{
    public enum Reach
    {
        Checking,
        /// <summary>UPnP opened the port and the router has a public address. Players can join with no port forwarding.</summary>
        OpenViaUpnp,
        /// <summary>UPnP did not work. The player must forward the port by hand.</summary>
        NeedsPortForward,
        /// <summary>The ISP shares one public address between customers. Port forwarding cannot work.</summary>
        Cgnat,
        /// <summary>Could not find the public address. LAN only.</summary>
        Offline
    }

    public sealed class ReachabilityReport
    {
        public Reach State = Reach.Checking;
        public string PublicIp, RouterWanIp, LocalIp, Message;
        public int Port;
        /// <summary>What a friend types to join over the internet (ip:port), or null when no internet join is possible.</summary>
        public string JoinAddress;
        public string LanAddress;
        public bool PortForwardNeeded => State == Reach.NeedsPortForward || State == Reach.Cgnat;
    }

    public static class Reachability
    {
        public static bool IsPrivateOrCgnat(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // 100.64.0.0/10 carrier-grade NAT
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        /// <summary>
        /// Combines what UPnP and STUN found into plain advice.
        /// publicIp: our address seen from outside (STUN). routerWanIp: the address the router says it has (UPnP).
        /// If those two differ, another NAT sits in front of the router, so forwarding a port cannot help.
        /// </summary>
        public static ReachabilityReport Classify(string publicIp, string routerWanIp, bool upnpMapped, string upnpError, string localIp, int port)
        {
            var r = new ReachabilityReport { PublicIp = publicIp, RouterWanIp = routerWanIp, LocalIp = localIp, Port = port };
            if (localIp != null) r.LanAddress = localIp + ":" + port;

            if (publicIp == null)
            {
                r.State = Reach.Offline;
                r.Message = "Could not find your public address (no internet, or STUN is blocked). Players on your network can join at " +
                            (r.LanAddress ?? "your LAN address") + ". Steam join may still work.";
                return r;
            }

            IPAddress wan;
            bool wanKnown = routerWanIp != null && IPAddress.TryParse(routerWanIp, out wan) && !routerWanIp.Equals("0.0.0.0");
            bool cgnat = false;
            if (wanKnown)
            {
                var w = IPAddress.Parse(routerWanIp);
                cgnat = IsPrivateOrCgnat(w) || routerWanIp != publicIp;
            }

            if (cgnat)
            {
                r.State = Reach.Cgnat;
                r.Message = "Port forwarding will NOT work: your router's address (" + routerWanIp + ") is not your public address (" + publicIp +
                            "), so your internet provider shares one address between customers. Use Steam join, a VPN, or ask your provider for a public address.";
                return r;
            }

            if (upnpMapped)
            {
                r.State = Reach.OpenViaUpnp;
                r.JoinAddress = publicIp + ":" + port;
                r.Message = "Port " + port + " was opened automatically (UPnP). No port forwarding needed. Friends join at " + r.JoinAddress + ".";
                return r;
            }

            r.State = Reach.NeedsPortForward;
            r.JoinAddress = publicIp + ":" + port;
            r.Message = "You still need to port forward: UPnP did not work" + (upnpError != null ? " (" + upnpError + ")" : "") +
                        ". On your router, forward TCP port " + port + " to " + (localIp ?? "this computer") +
                        ", then friends join at " + r.JoinAddress + ". If that still fails your provider may use CGNAT: use Steam join.";
            return r;
        }
    }

    public enum JoinKind { Invalid, Tcp, Steam }

    public sealed class JoinTarget
    {
        public JoinKind Kind;
        public string Host;
        public int Port;
        public ulong SteamId;
        public string Error;

        public const int DefaultPort = 7777;

        /// <summary>Accepts "1.2.3.4", "1.2.3.4:7777", "host.name:7777", "steam:7656119...", or a bare 17 digit Steam id.</summary>
        public static JoinTarget Parse(string text)
        {
            var t = new JoinTarget { Kind = JoinKind.Invalid };
            if (text == null) { t.Error = "empty"; return t; }
            text = text.Trim();
            if (text.Length == 0) { t.Error = "empty"; return t; }

            string s = text;
            if (s.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6).Trim();
            ulong id;
            if (Regex.IsMatch(s, "^7656119\\d{10}$") && ulong.TryParse(s, out id)) { t.Kind = JoinKind.Steam; t.SteamId = id; return t; }
            if (text.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) { t.Error = "that is not a valid Steam id"; return t; }

            string host = text; int port = DefaultPort;
            int colon = text.LastIndexOf(':');
            if (colon >= 0 && text.IndexOf(':') == colon) // exactly one colon: host:port (IPv6 is not supported)
            {
                host = text.Substring(0, colon).Trim();
                int p;
                if (!int.TryParse(text.Substring(colon + 1).Trim(), out p) || p < 1 || p > 65535) { t.Error = "bad port"; return t; }
                port = p;
            }
            else if (colon >= 0) { t.Error = "IPv6 addresses are not supported yet"; return t; }
            if (host.Length == 0 || !Regex.IsMatch(host, "^[A-Za-z0-9]([A-Za-z0-9\\-\\.]*[A-Za-z0-9])?$")) { t.Error = "bad address"; return t; }
            t.Kind = JoinKind.Tcp; t.Host = host; t.Port = port;
            return t;
        }
    }

    /// <summary>
    /// Runs UPnP and the public-address check in the background and produces a ReachabilityReport.
    /// Poll Report from the main thread.
    /// </summary>
    public sealed class ReachabilityProbe : IDisposable
    {
        public volatile ReachabilityReport Report = new ReachabilityReport { Message = "Checking your connection..." };
        readonly UpnpMapper _upnp = new UpnpMapper();
        Thread _t;
        volatile bool _stop;

        public void Start(int port, Action<string> log = null)
        {
            Report = new ReachabilityReport { Port = port, Message = "Checking your connection..." };
            _upnp.Start(port, "Subnautica MP", log);
            _t = new Thread(() =>
            {
                string pub = null;
                try { var ip = Stun.QueryPublicIp(); pub = ip == null ? null : ip.ToString(); } catch { }
                // wait for UPnP to finish (search takes up to ~5 s)
                for (int i = 0; i < 120 && !_stop && _upnp.State == UpnpState.Searching; i++) Thread.Sleep(100);
                if (_stop) return;
                Report = Reachability.Classify(pub, _upnp.ExternalIp, _upnp.State == UpnpState.Mapped, _upnp.Error, _upnp.LocalIp, port);
            }) { IsBackground = true, Name = "mp-reach" };
            _t.Start();
        }

        public void Dispose() { _stop = true; _upnp.Dispose(); }
    }
}
