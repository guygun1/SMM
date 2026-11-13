using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace MPCore.Net
{
    /// <summary>Pure protocol helpers for UPnP IGD. No sockets, so they are unit tested.</summary>
    public static class UpnpProto
    {
        public static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
            "upnp:rootdevice"
        };

        public static string BuildSearch(string st)
        {
            return "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n";
        }

        public static string ParseHeader(string http, string name)
        {
            var m = Regex.Match(http, "^" + Regex.Escape(name) + ":\\s*(.+?)\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Finds the WANIPConnection (or WANPPPConnection) service and its absolute control URL.</summary>
        public static bool ParseDescription(string xml, Uri location, out string controlUrl, out string serviceType)
        {
            controlUrl = null; serviceType = null;
            var doc = new XmlDocument();
            try { doc.LoadXml(xml); } catch { return false; }

            Uri baseUri = location;
            var ub = doc.GetElementsByTagName("URLBase");
            if (ub.Count > 0 && !string.IsNullOrEmpty(ub[0].InnerText))
            {
                Uri u;
                if (Uri.TryCreate(ub[0].InnerText.Trim(), UriKind.Absolute, out u)) baseUri = u;
            }

            string bestCtl = null, bestType = null;
            foreach (XmlNode svc in doc.GetElementsByTagName("service"))
            {
                string type = null, ctl = null;
                foreach (XmlNode ch in svc.ChildNodes)
                {
                    if (ch.LocalName == "serviceType") type = ch.InnerText.Trim();
                    else if (ch.LocalName == "controlURL") ctl = ch.InnerText.Trim();
                }
                if (type == null || ctl == null) continue;
                bool ip = type.Contains("WANIPConnection"), ppp = type.Contains("WANPPPConnection");
                if (!ip && !ppp) continue;
                if (bestType == null || ip) { bestType = type; bestCtl = ctl; if (ip) break; }
            }
            if (bestCtl == null) return false;

            Uri abs;
            if (!Uri.TryCreate(baseUri, bestCtl, out abs)) return false;
            controlUrl = abs.ToString();
            serviceType = bestType;
            return true;
        }

        public static string BuildSoap(string serviceType, string action, IList<KeyValuePair<string, string>> args)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");
            if (args != null)
                foreach (var kv in args)
                    sb.Append('<').Append(kv.Key).Append('>').Append(SecurityElement.Escape(kv.Value ?? "")).Append("</").Append(kv.Key).Append('>');
            sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            return sb.ToString();
        }

        public static string ParseTag(string xml, string tag)
        {
            var m = Regex.Match(xml, "<(?:\\w+:)?" + Regex.Escape(tag) + "[^>]*>(.*?)</(?:\\w+:)?" + Regex.Escape(tag) + ">", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>Returns "code: description" for a SOAP fault, or null.</summary>
        public static string ParseFault(string xml)
        {
            if (xml == null) return null;
            string code = ParseTag(xml, "errorCode");
            if (code == null) return null;
            return code + ": " + (ParseTag(xml, "errorDescription") ?? "?");
        }
    }

    public sealed class UpnpGateway
    {
        public string ControlUrl, ServiceType, LocalAddress, Location;
    }

    /// <summary>Blocking UPnP client. Run it off the main thread (see UpnpMapper).</summary>
    public static class UpnpClient
    {
        /// <summary>
        /// Opens one SSDP socket per up IPv4 interface. Each socket is bound to that interface's address and
        /// set as the multicast egress, so the search leaves on the right NIC and the router's reply comes back to it.
        /// A socket on IPAddress.Any can send out the wrong NIC (VPN, Hyper-V, Docker) and the router never answers.
        /// </summary>
        static List<Socket> OpenSsdpSockets(Action<string> log)
        {
            var socks = new List<Socket>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props = ni.GetIPProperties();
                int index;
                try { index = props.GetIPv4Properties().Index; }
                catch (Exception) { continue; } // no IPv4 on this interface
                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    IPAddress local = ua.Address;
                    if (local.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(local)) continue;
                    Socket s = null;
                    try
                    {
                        s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        s.Bind(new IPEndPoint(local, 0));
                        try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(index)); }
                        catch (Exception e) { log?.Invoke("ssdp interface " + local + ": " + e.Message); }
                        socks.Add(s);
                        s = null;
                    }
                    catch (Exception e) { log?.Invoke("ssdp bind " + local + ": " + e.Message); }
                    finally { if (s != null) s.Close(); }
                }
            }
            return socks;
        }

        public static UpnpGateway Discover(int timeoutMs, Action<string> log)
        {
            var seen = new HashSet<string>();
            var socks = OpenSsdpSockets(log);
            if (socks.Count == 0)
            {
                // no IPv4 interface could be listed: fall back to the default route
                var fallback = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                fallback.Bind(new IPEndPoint(IPAddress.Any, 0));
                socks.Add(fallback);
            }
            try
            {
                var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var s in socks)
                    foreach (var st in UpnpProto.SearchTargets)
                    {
                        var b = Encoding.ASCII.GetBytes(UpnpProto.BuildSearch(st));
                        try { s.SendTo(b, dest); } catch (Exception e) { log?.Invoke("ssdp send: " + e.Message); }
                    }
                var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var buf = new byte[4096];
                while (DateTime.UtcNow < end)
                {
                    var readable = new List<Socket>(socks);
                    try { Socket.Select(readable, null, null, 400 * 1000); }
                    catch (SocketException) { break; }
                    foreach (var s in readable)
                    {
                        int n;
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        try { n = s.ReceiveFrom(buf, ref from); }
                        catch (SocketException) { continue; }
                        var resp = Encoding.ASCII.GetString(buf, 0, n);
                        var loc = UpnpProto.ParseHeader(resp, "LOCATION");
                        Uri locUri;
                        if (loc == null || !Uri.TryCreate(loc, UriKind.Absolute, out locUri) || !seen.Add(loc)) continue;
                        try
                        {
                            var xml = HttpGet(loc, 3000);
                            string ctl, type;
                            if (!UpnpProto.ParseDescription(xml, locUri, out ctl, out type)) continue;
                            return new UpnpGateway { ControlUrl = ctl, ServiceType = type, Location = loc, LocalAddress = LocalAddressFor(locUri) };
                        }
                        catch (Exception e) { log?.Invoke("upnp description " + loc + ": " + e.Message); }
                    }
                }
                return null;
            }
            finally
            {
                foreach (var s in socks) s.Close();
            }
        }

        static string LocalAddressFor(Uri remote)
        {
            using (var s = new UdpClient())
            {
                s.Connect(remote.Host, remote.Port);
                return ((IPEndPoint)s.Client.LocalEndPoint).Address.ToString();
            }
        }

        static string HttpGet(string url, int timeout)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Timeout = timeout; rq.ReadWriteTimeout = timeout;
            using (var rs = rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        /// <summary>Returns the response body, or null with fault set.</summary>
        public static string Soap(UpnpGateway g, string action, IList<KeyValuePair<string, string>> args, out string fault)
        {
            fault = null;
            var body = Encoding.UTF8.GetBytes(UpnpProto.BuildSoap(g.ServiceType, action, args));
            var rq = (HttpWebRequest)WebRequest.Create(g.ControlUrl);
            rq.Method = "POST"; rq.Timeout = 4000; rq.ReadWriteTimeout = 4000;
            rq.ContentType = "text/xml; charset=\"utf-8\"";
            rq.Headers.Add("SOAPACTION", "\"" + g.ServiceType + "#" + action + "\"");
            rq.ContentLength = body.Length;
            try
            {
                using (var rs = rq.GetRequestStream()) rs.Write(body, 0, body.Length);
                using (var resp = rq.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch (WebException e)
            {
                string txt = null;
                if (e.Response != null)
                    using (var sr = new StreamReader(e.Response.GetResponseStream(), Encoding.UTF8)) txt = sr.ReadToEnd();
                fault = UpnpProto.ParseFault(txt) ?? e.Message;
                return null;
            }
        }

        public static string GetExternalIp(UpnpGateway g)
        {
            string f;
            var r = Soap(g, "GetExternalIPAddress", null, out f);
            return r == null ? null : UpnpProto.ParseTag(r, "NewExternalIPAddress");
        }

        public static bool AddMapping(UpnpGateway g, int port, string proto, string desc, int lease, out string error)
        {
            var args = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", port.ToString()),
                new KeyValuePair<string, string>("NewProtocol", proto),
                new KeyValuePair<string, string>("NewInternalPort", port.ToString()),
                new KeyValuePair<string, string>("NewInternalClient", g.LocalAddress),
                new KeyValuePair<string, string>("NewEnabled", "1"),
                new KeyValuePair<string, string>("NewPortMappingDescription", desc),
                new KeyValuePair<string, string>("NewLeaseDuration", lease.ToString())
            };
            string f;
            var r = Soap(g, "AddPortMapping", args, out f);
            error = f;
            return r != null;
        }

        public static void DeleteMapping(UpnpGateway g, int port, string proto)
        {
            string f;
            Soap(g, "DeletePortMapping", new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", port.ToString()),
                new KeyValuePair<string, string>("NewProtocol", proto)
            }, out f);
        }
    }

    public enum UpnpState { Idle, Searching, Mapped, Failed }

    /// <summary>Opens a TCP port on the router in the background and keeps it open. Read the fields from any thread.</summary>
    public sealed class UpnpMapper : IDisposable
    {
        public volatile UpnpState State = UpnpState.Idle;
        public volatile string ExternalIp, LocalIp, Error;
        public int Port { get; private set; }

        Thread _t;
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        volatile UpnpGateway _gw;
        volatile bool _weCreatedMapping; // true once AddPortMapping has succeeded, whatever State says now
        Action<string> _log;

        // delays before retrying a refused mapping. After the last retry the mapper gives up and sets Failed.
        static readonly int[] RetryDelaysMs = { 5000, 15000, 30000 };

        public void Start(int port, string description, Action<string> log = null)
        {
            Port = port; _log = log; State = UpnpState.Searching;
            _t = new Thread(() => Run(description)) { IsBackground = true, Name = "mp-upnp" };
            _t.Start();
        }

        void Run(string desc)
        {
            try
            {
                _gw = UpnpClient.Discover(5000, _log);
                if (_gw == null)
                {
                    Error = "no UPnP router answered (UPnP is probably switched off on the router)";
                    State = UpnpState.Failed; return;
                }
                LocalIp = _gw.LocalAddress;
                ExternalIp = UpnpClient.GetExternalIp(_gw);
                int failures = 0;
                while (!_stop.WaitOne(0))
                {
                    string err;
                    bool ok = AddWithConflictFix(desc, 3600, out err);
                    if (!ok && err != null && err.StartsWith("725")) ok = AddWithConflictFix(desc, 0, out err); // router only allows permanent leases
                    if (ok)
                    {
                        failures = 0;
                        _weCreatedMapping = true;
                        if (_stop.WaitOne(0))
                        {
                            // Dispose ran while the add was in flight: remove the mapping we just made.
                            try { UpnpClient.DeleteMapping(_gw, Port, "TCP"); } catch { }
                            break;
                        }
                        Error = null; State = UpnpState.Mapped;
                        if (_stop.WaitOne(30 * 60 * 1000)) break; // renew every 30 min
                        continue;
                    }
                    // Refused: keep the current state and retry with backoff. Give up only after the last retry.
                    failures++;
                    Error = "router refused the port mapping (" + (err ?? "no response") + ")";
                    if (failures > RetryDelaysMs.Length) { State = UpnpState.Failed; return; }
                    if (_stop.WaitOne(RetryDelaysMs[failures - 1])) break;
                }
            }
            catch (Exception e) { Error = e.Message; State = UpnpState.Failed; }
        }

        bool AddWithConflictFix(string desc, int lease, out string err)
        {
            bool ok = UpnpClient.AddMapping(_gw, Port, "TCP", desc, lease, out err);
            if (!ok && err != null && err.StartsWith("718"))
            {
                // 718 ConflictInMappingEntry: a stale entry (left by a crash, or by another program) holds the port.
                // Remove it and add ours again.
                UpnpClient.DeleteMapping(_gw, Port, "TCP");
                ok = UpnpClient.AddMapping(_gw, Port, "TCP", desc, lease, out err);
            }
            return ok;
        }

        public void Dispose()
        {
            _stop.Set();
            var gw = _gw;
            if (gw != null && _weCreatedMapping)
            {
                _weCreatedMapping = false;
                int port = Port;
                // Delete on a background thread so the game thread never waits on the router (a request can take up to 4 s).
                var t = new Thread(() => { try { UpnpClient.DeleteMapping(gw, port, "TCP"); } catch { } }) { IsBackground = true, Name = "mp-upnp-del" };
                t.Start();
                t.Join(1000);
            }
            State = UpnpState.Idle;
        }
    }
}
