using System;
using System.Net;
using System.Net.Sockets;

namespace MPCore.Net
{
    /// <summary>Minimal STUN client (RFC 5389 binding request). Only used to learn our public address.</summary>
    public static class Stun
    {
        const uint Magic = 0x2112A442;

        public static byte[] BuildBindingRequest(byte[] txid)
        {
            var b = new byte[20];
            b[0] = 0x00; b[1] = 0x01;           // binding request
            b[4] = 0x21; b[5] = 0x12; b[6] = 0xA4; b[7] = 0x42;
            Buffer.BlockCopy(txid, 0, b, 8, 12);
            return b;
        }

        public static IPEndPoint ParseBindingResponse(byte[] d, byte[] txid)
        {
            if (d == null || d.Length < 20 || d[0] != 0x01 || d[1] != 0x01) return null;
            if (d[4] != 0x21 || d[5] != 0x12 || d[6] != 0xA4 || d[7] != 0x42) return null;
            for (int i = 0; i < 12; i++) if (d[8 + i] != txid[i]) return null;
            int len = (d[2] << 8) | d[3];
            int p = 20, end = Math.Min(d.Length, 20 + len);
            IPEndPoint plain = null;
            while (p + 4 <= end)
            {
                int type = (d[p] << 8) | d[p + 1], alen = (d[p + 2] << 8) | d[p + 3];
                int v = p + 4;
                if (v + alen > end) break;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && d[v + 1] == 0x01) // IPv4
                {
                    int port = (d[v + 2] << 8) | d[v + 3];
                    var ip = new byte[4];
                    Buffer.BlockCopy(d, v + 4, ip, 0, 4);
                    if (type == 0x0020)
                    {
                        port ^= (int)(Magic >> 16);
                        ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42;
                        return new IPEndPoint(new IPAddress(ip), port);
                    }
                    plain = new IPEndPoint(new IPAddress(ip), port);
                }
                p = v + ((alen + 3) & ~3);
            }
            return plain;
        }

        /// <summary>Blocking. Returns our public IPv4 address as seen from outside, or null.</summary>
        public static IPAddress QueryPublicIp(string host, int port, int timeoutMs)
        {
            try
            {
                var txid = new byte[12];
                new Random().NextBytes(txid);
                using (var udp = new UdpClient())
                {
                    udp.Connect(host, port);
                    var req = BuildBindingRequest(txid);
                    udp.Send(req, req.Length);
                    // Read until the deadline. Stray or late packets are skipped, and ParseBindingResponse
                    // rejects any reply whose transaction id does not match ours.
                    var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (true)
                    {
                        int remaining = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                        if (remaining <= 0) return null;
                        udp.Client.ReceiveTimeout = remaining;
                        try
                        {
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var resp = udp.Receive(ref from);
                            var ep = ParseBindingResponse(resp, txid);
                            if (ep != null) return ep.Address;
                        }
                        catch (SocketException) { System.Threading.Thread.Sleep(10); } // timeout or ICMP error: the deadline check above ends the loop
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Tries a few public servers.</summary>
        public static IPAddress QueryPublicIp(int timeoutMs = 2500)
        {
            var servers = new[] { "stun.l.google.com:19302", "stun.cloudflare.com:3478", "stun1.l.google.com:19302" };
            foreach (var s in servers)
            {
                int c = s.IndexOf(':');
                var ip = QueryPublicIp(s.Substring(0, c), int.Parse(s.Substring(c + 1)), timeoutMs);
                if (ip != null) return ip;
            }
            return null;
        }
    }
}
