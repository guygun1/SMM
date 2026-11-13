using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MPCore.IO;
using MPCore.Net;
using MPCore.Sync;

enum Mood { Calm, Angry }

class Health
{
    [Synced] public int hp;
    [Synced(Quantize = 0.1f)] public float x;
    [Synced] public string label;
    [Synced] public List<int> ids = new List<int>();
    [Synced] public NetRef target;
    [Synced] public Mood mood;
    public int localOnly; // not synced
}

static class T
{
    static int _fail, _pass;
    static string _cur;

    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine("  FAIL [" + _cur + "] " + what);
    }

    class Rig
    {
        public LoopbackHub Hub = new LoopbackHub();
        public SyncSession Host;
        public List<SyncSession> Clients = new List<SyncSession>();
        public List<DesyncReport> Reports = new List<DesyncReport>();
        public Dictionary<SyncSession, List<Health>> Spawned = new Dictionary<SyncSession, List<Health>>();

        public Rig(ModInfo[] hostMods = null)
        {
            Host = SyncSession.CreateHost(Hub.CreateHost());
            Host.Name = "host"; Host.SendIntervalMs = 0; Host.AuditIntervalMs = 0;
            if (hostMods != null) Host.Mods = hostMods;
            Wire(Host);
        }

        void Wire(SyncSession s)
        {
            Spawned[s] = new List<Health>();
            s.Desync += r => Reports.Add(r);
            // the adapter's job: build the thing and bind its components
            s.SpawnReceived += o =>
            {
                var h = new Health();
                s.Bind(o, "Health", h);
                o.UserData = h;
                Spawned[s].Add(h);
            };
        }

        public SyncSession Join(string name, ModInfo[] mods = null)
        {
            var c = SyncSession.CreateClient(Hub.CreateClient());
            c.Name = name; c.SendIntervalMs = 0; c.AuditIntervalMs = 0;
            if (mods != null) c.Mods = mods;
            Wire(c);
            Clients.Add(c);
            Pump();
            return c;
        }

        public void Pump(int rounds = 8)
        {
            for (int i = 0; i < rounds; i++)
            {
                Host.Tick();
                foreach (var c in Clients) c.Tick();
            }
        }
    }

    static void Run(string name, Action a)
    {
        _cur = name;
        try { a(); Console.WriteLine("ok   " + name); }
        catch (Exception e) { _fail++; Console.WriteLine("EXC  " + name + ": " + e); }
    }

    static int Main()
    {
        SyncRegistry.Register<Health>();

        Run("bytes roundtrip", () =>
        {
            var w = new ByteWriter();
            w.VarI(-1); w.VarI(long.MinValue); w.VarU(ulong.MaxValue); w.F32(1.5f); w.F64(-2.25); w.Str("héllo"); w.Str(null); w.Str("");
            w.Bytes(new byte[] { 1, 2, 3 }); w.Bytes(null);
            var r = new ByteReader(w.ToArray());
            Check(r.VarI() == -1, "-1"); Check(r.VarI() == long.MinValue, "min"); Check(r.VarU() == ulong.MaxValue, "max");
            Check(r.F32() == 1.5f, "f32"); Check(r.F64() == -2.25, "f64"); Check(r.Str() == "héllo", "str");
            Check(r.Str() == null, "null str"); Check(r.Str() == "", "empty str");
            Check(r.Bytes().SequenceEqual(new byte[] { 1, 2, 3 }), "bytes"); Check(r.Bytes() == null, "null bytes");
            Check(r.Remaining == 0, "consumed");
            bool threw = false;
            try { r.U8(); } catch (FormatException) { threw = true; }
            Check(threw, "underflow throws");
        });

        Run("registry rules", () =>
        {
            var st = SyncRegistry.Register<Health>();
            Check(st.Fields.Select(f => f.Name).SequenceEqual(new[] { "hp", "ids", "label", "mood", "target", "x" }), "fields sorted, localOnly skipped");
            bool threw = false;
            try { SyncRegistry.Register(typeof(Bad), null, null); } catch (ArgumentException) { threw = true; }
            Check(threw, "type without codec rejected loudly");
            // opt-in by name for a type we do not own
            var st2 = SyncRegistry.Register(typeof(ThirdParty), new[] { "gold" }, "SomeMod");
            Check(st2.ModId == "SomeMod" && st2.Fields.Length == 1, "name based registration");
        });

        Run("handshake + spawn + state", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var b = rig.Join("B");
            Check(a.Ready && b.Ready && a.LocalPeer == 1 && b.LocalPeer == 2, "joined with distinct peer ids");

            var o = a.Spawn("hero");
            var h = new Health(); a.Bind(o, "Health", h);
            h.hp = 77; h.x = 1.23f; h.label = "bob"; h.ids = new List<int> { 4, 5 }; h.mood = Mood.Angry; h.target = new NetRef(99);
            rig.Pump();

            Check(rig.Spawned[rig.Host].Count == 1 && rig.Spawned[b].Count == 1, "spawn reached host and other client");
            Check(rig.Spawned[a].Count == 0, "owner did not get an echo spawn");
            foreach (var peer in new[] { rig.Host, b })
            {
                var r = rig.Spawned[peer][0];
                Check(r.hp == 77 && r.label == "bob" && r.mood == Mood.Angry && r.target.Id == 99, "scalar fields on " + peer.LocalPeer);
                Check(r.ids.SequenceEqual(new[] { 4, 5 }), "list on " + peer.LocalPeer);
                Check(Math.Abs(r.x - 1.2f) < 0.001f, "quantized float on " + peer.LocalPeer + " got " + r.x);
            }

            h.hp = 10; rig.Pump();
            Check(rig.Spawned[b][0].hp == 10, "delta update");
            Check(rig.Reports.Count == 0, "no reports on clean run: " + string.Join("|", rig.Reports));
        });

        Run("only changed fields are sent", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h);
            h.hp = 1; rig.Pump();
            int sent = 0;
            rig.Hub.Drop = (from, to, d) => { if (from == 1 && d[0] == 7) sent++; return false; };
            rig.Pump(5);
            Check(sent == 0, "idle object sends nothing, sent " + sent);
            h.hp = 2; rig.Pump();
            Check(sent == 1, "one change, one message, sent " + sent);
        });

        Run("late joiner gets full state", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h);
            h.hp = 55; h.label = "x"; rig.Pump();
            var c = rig.Join("C");
            rig.Pump();
            Check(rig.Spawned[c].Count == 1, "late joiner got spawn");
            Check(rig.Spawned[c].Count == 1 && rig.Spawned[c][0].hp == 55 && rig.Spawned[c][0].label == "x", "late joiner got state without owner resending");
        });

        Run("state before bind is buffered", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var late = new List<NetObject>();
            // client B binds its component one tick after the spawn (like an async prefab load)
            var b = SyncSession.CreateClient(rig.Hub.CreateClient());
            b.SendIntervalMs = 0; b.AuditIntervalMs = 0; rig.Clients.Add(b);
            b.SpawnReceived += o => late.Add(o);
            var o1 = a.Spawn("hero"); var h = new Health(); a.Bind(o1, "Health", h); h.hp = 33;
            rig.Pump();
            Check(late.Count == 1, "b saw spawn");
            var bh = new Health(); b.Bind(late[0], "Health", bh);
            Check(bh.hp == 33, "buffered state applied at bind, hp=" + bh.hp);
        });

        Run("ownership violation blocked", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h); h.hp = 5; rig.Pump();

            // B forges a state packet for A's object
            var w = new ByteWriter();
            w.U8(7); w.VarU(o.Id); w.VarU(Hash.Str("Health")); w.VarU(SyncRegistry.Register<Health>().Id);
            w.VarU(1UL << 0 /* field hp... fields sorted: hp is index 0 */); w.VarI(9999);
            rig.Hub.End(2).Send(0, w.ToArray());
            rig.Pump();

            Check(h.hp == 5 && rig.Spawned[rig.Host][0].hp == 5 && rig.Spawned[b][0].hp == 5, "forged write changed nothing anywhere");
            Check(rig.Reports.Any(r => r.Kind == DesyncKind.OwnershipViolation), "violation reported");
        });

        Run("ownership transfer", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h); h.hp = 1; rig.Pump();
            var bo = b.Find(o.Id);
            b.RequestOwnership(bo); rig.Pump();
            Check(bo.Owner == 2 && o.Owner == 2 && rig.Host.Find(o.Id).Owner == 2, "owner is B everywhere");
            var bh = (Health)bo.UserData; bh.hp = 42; rig.Pump();
            Check(h.hp == 42, "new owner's writes reach old owner");
        });

        Run("local overwrite detected and repaired", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h); h.hp = 100; rig.Pump();
            var bh = rig.Spawned[b][0];
            bh.hp = 1; // some other mod on B pokes a synced field
            foreach (var s in new[] { a, b, rig.Host }) s.AuditIntervalMs = 1;
            Thread.Sleep(5); rig.Pump();
            Check(bh.hp == 100, "restored to owner's value, got " + bh.hp);
            var rep = rig.Reports.FirstOrDefault(r => r.Kind == DesyncKind.LocalOverwrite);
            Check(rep != null && rep.Field == "hp" && rep.Mod == typeof(Health).Assembly.GetName().Name, "attributed to field+mod: " + rep);
            Check(!rig.Reports.Any(r => r.Kind == DesyncKind.LostUpdate), "not blamed on the network layer");
        });

        Run("lost update detected and healed", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h); h.hp = 1; rig.Pump();
            bool dropped = false;
            rig.Hub.Drop = (from, to, d) => { if (!dropped && from == 0 && to == 2 && d[0] == 7) { dropped = true; return true; } return false; };
            h.hp = 2; rig.Pump();
            Check(dropped && rig.Spawned[b][0].hp == 1, "b missed the update");
            rig.Hub.Drop = null;
            foreach (var s in new[] { a, b, rig.Host }) s.AuditIntervalMs = 1;
            for (int i = 0; i < 4; i++) { Thread.Sleep(5); rig.Pump(); }
            Check(rig.Spawned[b][0].hp == 2, "healed via resync, hp=" + rig.Spawned[b][0].hp);
            Check(rig.Reports.Any(r => r.Kind == DesyncKind.LostUpdate), "reported as network layer problem");
        });

        Run("different mods: warned, can join anyway", () =>
        {
            var rig = new Rig(new[] { new ModInfo { Id = "CoolMod", Version = "1.0" }, new ModInfo { Id = "Skin", Version = "1", ClientOnly = true } });
            List<ModDiff> diffs = null; string why = null;
            var c = SyncSession.CreateClient(rig.Hub.CreateClient());
            c.ModsDiffer += d => diffs = d; c.Rejected += w => why = w;
            c.Mods = new[] { new ModInfo { Id = "CoolMod", Version = "2.0" }, new ModInfo { Id = "Other", Version = "1" } };
            rig.Clients.Add(c); rig.Pump();
            Check(diffs != null && why == null, "told about the differences, not rejected");
            Check(diffs != null && diffs.Any(d => d.ModId == "CoolMod" && d.HostVersion == "1.0" && d.YourVersion == "2.0"), "version difference listed");
            Check(diffs != null && diffs.Any(d => d.ModId == "Other" && d.HostVersion == null), "joiner-only mod listed");
            Check(!c.Ready, "not joined yet");

            var again = SyncSession.CreateClient(rig.Hub.CreateClient());
            again.Mods = c.Mods; again.AcceptModDifferences = true;
            rig.Clients.Add(again); rig.Pump();
            Check(again.Ready, "joins after confirming");

            var ok = rig.Join("fine", new[] { new ModInfo { Id = "CoolMod", Version = "1.0" } });
            Check(ok.Ready, "client-only mod difference allowed");
        });

        Run("required mod must match", () =>
        {
            var rig = new Rig(new[] { new ModInfo { Id = "mp", Version = "1.0" } });
            rig.Host.RequiredMods.Add("mp");
            string why = null;
            var c = SyncSession.CreateClient(rig.Hub.CreateClient());
            c.Rejected += w => why = w; c.AcceptModDifferences = true;
            c.Mods = new[] { new ModInfo { Id = "mp", Version = "0.9" } };
            rig.Clients.Add(c); rig.Pump();
            Check(why != null && why.Contains("mp") && !c.Ready, "rejected even when accepting differences: " + why);
        });

        Run("custom messages route", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var got = new List<string>();
            foreach (var s in new[] { rig.Host, a, b })
            {
                var id = s.LocalPeer;
                s.RegisterMessage("chat", (from, r) => got.Add(id + "<-" + from + ":" + r.Str()));
            }
            var w = new ByteWriter(); w.Str("hi");
            a.SendMessage("chat", w); rig.Pump();
            Check(got.Count == 2 && got.Contains("0<-1:hi") && got.Contains("2<-1:hi"), "broadcast reaches host and B, not sender: " + string.Join(",", got));
            got.Clear();
            b.SendMessage("chat", w, 1); rig.Pump();
            Check(got.SequenceEqual(new[] { "1<-2:hi" }), "direct to A through host: " + string.Join(",", got));
        });

        Run("peer leaving cleans up", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var o = a.Spawn("hero"); var h = new Health(); a.Bind(o, "Health", h); rig.Pump();
            var left = new List<int>(); b.PeerLeft += p => left.Add(p);
            a.Dispose(); rig.Clients.Remove(a); rig.Pump();
            Check(rig.Host.Find(o.Id) == null && b.Find(o.Id) == null, "owned object despawned everywhere");
            Check(left.SequenceEqual(new[] { 1 }), "peer left event");
        });

        Run("scene objects", () =>
        {
            var rig = new Rig();
            var hs = rig.Host.RegisterSceneObject("Scene/Door1"); var hh = new Health(); rig.Host.Bind(hs, "Health", hh); hh.hp = 8;
            rig.Pump();
            var a = rig.Join("A");
            var ao = a.RegisterSceneObject("Scene/Door1"); var ah = new Health(); a.Bind(ao, "Health", ah);
            rig.Pump();
            Check(ah.hp == 8, "client loaded scene later and still got host state, hp=" + ah.hp);
            Check(ao.Id == hs.Id, "same id on both sides");
        });

        Run("tcp end to end", () =>
        {
            var ht = new TcpHostTransport(0);
            var host = SyncSession.CreateHost(ht); host.SendIntervalMs = 0; host.AuditIntervalMs = 0;
            var ct = new TcpClientTransport("127.0.0.1", ht.Port);
            var cl = SyncSession.CreateClient(ct); cl.SendIntervalMs = 0; cl.AuditIntervalMs = 0;
            var got = new List<Health>();
            host.SpawnReceived += o => { var x = new Health(); host.Bind(o, "Health", x); got.Add(x); };
            for (int i = 0; i < 200 && !cl.Ready; i++) { host.Tick(); cl.Tick(); Thread.Sleep(5); }
            Check(cl.Ready, "client joined over real sockets");
            var o2 = cl.Spawn("hero"); var h = new Health(); cl.Bind(o2, "Health", h); h.hp = 123; h.label = "tcp";
            for (int i = 0; i < 200 && got.Count == 0; i++) { host.Tick(); cl.Tick(); Thread.Sleep(5); }
            for (int i = 0; i < 20; i++) { host.Tick(); cl.Tick(); Thread.Sleep(2); }
            Check(got.Count == 1 && got[0].hp == 123 && got[0].label == "tcp", "state crossed the wire");
            cl.Dispose(); host.Dispose();
        });

        Run("upnp protocol parsing", () =>
        {
            var desc = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceList><device><deviceList><device><serviceList>" +
                       "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>/ctl/PPP</controlURL></service>" +
                       "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL></service>" +
                       "</serviceList></device></deviceList></device></deviceList></device></root>";
            string ctl, type;
            Check(UpnpProto.ParseDescription(desc, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out ctl, out type), "parsed");
            Check(ctl == "http://192.168.1.1:5000/ctl/IPConn" && type.EndsWith("WANIPConnection:1"), "prefers WANIPConnection, absolute url: " + ctl);

            var withBase = desc.Replace("<device>", "<URLBase>http://10.0.0.1:49152/</URLBase><device>");
            UpnpProto.ParseDescription(withBase, new Uri("http://192.168.1.1:5000/x.xml"), out ctl, out type);
            Check(ctl == "http://10.0.0.1:49152/ctl/IPConn", "URLBase wins: " + ctl);
            Check(!UpnpProto.ParseDescription("<root/>", new Uri("http://a/"), out ctl, out type), "no WAN service -> false");
            Check(!UpnpProto.ParseDescription("not xml", new Uri("http://a/"), out ctl, out type), "bad xml -> false");

            var resp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLocation: http://192.168.1.1:5000/rootDesc.xml\r\nST: upnp:rootdevice\r\n\r\n";
            Check(UpnpProto.ParseHeader(resp, "LOCATION") == "http://192.168.1.1:5000/rootDesc.xml", "location header, any case");

            var soap = UpnpProto.BuildSoap("urn:x:service:WANIPConnection:1", "AddPortMapping",
                new[] { new KeyValuePair<string, string>("NewPortMappingDescription", "a<b>&c") });
            Check(soap.Contains("<NewPortMappingDescription>a&lt;b&gt;&amp;c</NewPortMappingDescription>") && soap.Contains("<u:AddPortMapping xmlns:u=\"urn:x:service:WANIPConnection:1\">"), "soap escaped");
            Check(UpnpProto.ParseTag("<s:Body><u:R><NewExternalIPAddress>203.0.113.9</NewExternalIPAddress></u:R></s:Body>", "NewExternalIPAddress") == "203.0.113.9", "tag");
            Check(UpnpProto.ParseTag("<a><u:NewExternalIPAddress xmlns:u=\"q\">1.2.3.4</u:NewExternalIPAddress></a>", "NewExternalIPAddress") == "1.2.3.4", "prefixed tag");
            Check(UpnpProto.ParseFault("<detail><UPnPError><errorCode>725</errorCode><errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError></detail>") == "725: OnlyPermanentLeasesSupported", "fault");
            Check(UpnpProto.ParseFault("<ok/>") == null, "no fault");
        });

        Run("stun parsing", () =>
        {
            var tx = new byte[12]; for (int i = 0; i < 12; i++) tx[i] = (byte)(i + 1);
            var req = Stun.BuildBindingRequest(tx);
            Check(req.Length == 20 && req[1] == 1 && req[4] == 0x21 && req[19] == 12, "request header");

            int port = 54321; byte[] ip = { 203, 0, 113, 9 };
            var rsp = new byte[32];
            rsp[0] = 0x01; rsp[1] = 0x01; rsp[3] = 12;
            rsp[4] = 0x21; rsp[5] = 0x12; rsp[6] = 0xA4; rsp[7] = 0x42;
            Buffer.BlockCopy(tx, 0, rsp, 8, 12);
            rsp[20] = 0x00; rsp[21] = 0x20; rsp[23] = 8; rsp[25] = 0x01;
            int xp = port ^ 0x2112; rsp[26] = (byte)(xp >> 8); rsp[27] = (byte)xp;
            rsp[28] = (byte)(ip[0] ^ 0x21); rsp[29] = (byte)(ip[1] ^ 0x12); rsp[30] = (byte)(ip[2] ^ 0xA4); rsp[31] = (byte)(ip[3] ^ 0x42);
            var ep = Stun.ParseBindingResponse(rsp, tx);
            Check(ep != null && ep.Address.ToString() == "203.0.113.9" && ep.Port == 54321, "xor-mapped address: " + ep);

            var wrong = new byte[12];
            Check(Stun.ParseBindingResponse(rsp, wrong) == null, "wrong transaction id rejected");
            Check(Stun.ParseBindingResponse(new byte[5], tx) == null, "short packet rejected");
        });

        Run("reachability advice", () =>
        {
            var open = Reachability.Classify("203.0.113.9", "203.0.113.9", true, null, "192.168.1.20", 7777);
            Check(open.State == Reach.OpenViaUpnp && open.JoinAddress == "203.0.113.9:7777" && !open.PortForwardNeeded, "upnp ok: " + open.Message);

            var fwd = Reachability.Classify("203.0.113.9", null, false, "no UPnP router answered", "192.168.1.20", 7777);
            Check(fwd.State == Reach.NeedsPortForward && fwd.PortForwardNeeded && fwd.Message.Contains("forward TCP port 7777 to 192.168.1.20"), "must forward: " + fwd.Message);

            var fwd2 = Reachability.Classify("203.0.113.9", "203.0.113.9", false, "router refused", "192.168.1.20", 7777);
            Check(fwd2.State == Reach.NeedsPortForward, "upnp failed but router has public ip -> forward by hand");

            var cg = Reachability.Classify("203.0.113.9", "100.72.5.4", true, null, "192.168.1.20", 7777);
            Check(cg.State == Reach.Cgnat && cg.JoinAddress == null && cg.Message.Contains("will NOT work"), "cgnat via 100.64/10 even though upnp mapped: " + cg.Message);

            var cg2 = Reachability.Classify("203.0.113.9", "198.51.100.7", true, null, "192.168.1.20", 7777);
            Check(cg2.State == Reach.OpenViaUpnp && cg2.JoinAddress == "203.0.113.9:7777" && cg2.Message.Contains("VPN"), "public router address that differs from the STUN one is only a warning (VPN / second link)");

            var cg3 = Reachability.Classify(null, "198.51.100.7", true, null, "192.168.1.20", 7777);
            Check(cg3.State == Reach.OpenViaUpnp && cg3.JoinAddress == "198.51.100.7:7777", "STUN failed but UPnP mapped on a public router address -> join through the router address");

            var off = Reachability.Classify(null, null, false, null, "192.168.1.20", 7777);
            Check(off.State == Reach.Offline && off.LanAddress == "192.168.1.20:7777", "offline keeps lan address");

            Check(Reachability.IsPrivateOrCgnat(System.Net.IPAddress.Parse("10.1.2.3")) && Reachability.IsPrivateOrCgnat(System.Net.IPAddress.Parse("172.20.0.1")) &&
                  Reachability.IsPrivateOrCgnat(System.Net.IPAddress.Parse("100.64.0.1")) && !Reachability.IsPrivateOrCgnat(System.Net.IPAddress.Parse("172.32.0.1")) &&
                  !Reachability.IsPrivateOrCgnat(System.Net.IPAddress.Parse("8.8.8.8")), "private ranges");
        });

        Run("join address parsing", () =>
        {
            var a = JoinTarget.Parse(" 203.0.113.9 ");
            Check(a.Kind == JoinKind.Tcp && a.Host == "203.0.113.9" && a.Port == 7777, "bare ip gets default port");
            var b = JoinTarget.Parse("my.host-name.org:9000");
            Check(b.Kind == JoinKind.Tcp && b.Host == "my.host-name.org" && b.Port == 9000, "host:port");
            var c = JoinTarget.Parse("steam:76561198000000001");
            Check(c.Kind == JoinKind.Steam && c.SteamId == 76561198000000001UL, "steam: prefix");
            Check(JoinTarget.Parse("76561198000000001").Kind == JoinKind.Steam, "bare steam id");
            Check(JoinTarget.Parse("1.2.3.4:99999").Kind == JoinKind.Invalid, "port range");
            Check(JoinTarget.Parse("1.2.3.4:abc").Kind == JoinKind.Invalid, "port text");
            Check(JoinTarget.Parse("").Kind == JoinKind.Invalid && JoinTarget.Parse(null).Kind == JoinKind.Invalid, "empty");
            Check(JoinTarget.Parse("bad host!").Kind == JoinKind.Invalid, "bad chars");
            Check(JoinTarget.Parse("steam:123").Kind == JoinKind.Invalid, "bad steam id");
        });

        Run("two transports, one host", () =>
        {
            var hubA = new LoopbackHub(); var hubB = new LoopbackHub();
            var host = SyncSession.CreateHost(new MultiHostTransport(hubA.CreateHost(), hubB.CreateHost()));
            host.SendIntervalMs = 0; host.AuditIntervalMs = 0;
            var a = SyncSession.CreateClient(hubA.CreateClient()); a.SendIntervalMs = 0; a.AuditIntervalMs = 0; // e.g. TCP player
            var b = SyncSession.CreateClient(hubB.CreateClient()); b.SendIntervalMs = 0; b.AuditIntervalMs = 0; // e.g. Steam player
            var bGot = new List<Health>();
            b.SpawnReceived += o => { var h2 = new Health(); b.Bind(o, "Health", h2); bGot.Add(h2); };
            for (int i = 0; i < 10; i++) { host.Tick(); a.Tick(); b.Tick(); }
            Check(a.Ready && b.Ready && a.LocalPeer != b.LocalPeer && a.LocalPeer > 0 && b.LocalPeer > 0, "distinct unified ids " + a.LocalPeer + "," + b.LocalPeer);
            var o1 = a.Spawn("hero"); var h = new Health(); a.Bind(o1, "Health", h); h.hp = 31;
            for (int i = 0; i < 10; i++) { host.Tick(); a.Tick(); b.Tick(); }
            Check(bGot.Count == 1 && bGot[0].hp == 31, "state crossed from one transport to the other");
        });

        Run("client to host direct message", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var got = new List<string>();
            rig.Host.RegisterMessage("req", (from, r) => got.Add("host<-" + from));
            a.RegisterMessage("req", (from, r) => got.Add("a<-" + from));
            a.SendMessage("req", new ByteWriter(), 0); rig.Pump();
            Check(got.SequenceEqual(new[] { "host<-1" }), "target 0 reaches only the host: " + string.Join(",", got));
            got.Clear();
            rig.Host.RegisterMessage("ans", (from, r) => got.Add("host<-" + from));
            a.RegisterMessage("ans", (from, r) => got.Add("a<-" + from + ":" + r.Str()));
            var w = new ByteWriter(); w.Str("yes");
            rig.Host.SendMessage("ans", w, a.LocalPeer); rig.Pump();
            Check(got.SequenceEqual(new[] { "a<-0:yes" }), "host to one client: " + string.Join(",", got));
        });

        Run("file transfer loopback", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A"); var b = rig.Join("B");
            var rnd = new Random(7);
            var files = new List<FileEntry>();
            foreach (var sz in new[] { 0, 1, 47999, 48000, 48001, 3000000, 12345 })
            {
                var d = new byte[sz]; rnd.NextBytes(d);
                files.Add(new FileEntry { Path = "dir" + sz % 3 + "/f" + sz + ".bin", Data = d });
            }
            var hostCh = new FileChannel(rig.Host, "wf");
            var aCh = new FileChannel(a, "wf");
            var bCh = new FileChannel(b, "wf");
            var outA = new Dictionary<string, MemoryStream>();
            aCh.CreateFile = path => { var m = new MemoryStream(); outA[path] = m; return m; };
            bool done = false, bGotSomething = false; string fail = null;
            aCh.ReceiveCompleted += () => done = true;
            aCh.ReceiveFailed += why => fail = why;
            bCh.ReceiveStarted += (n, t) => bGotSomething = true;
            hostCh.Send(a.LocalPeer, files);
            for (int i = 0; i < 2000 && !done && fail == null; i++) { hostCh.Tick(); rig.Pump(); }
            Check(done && fail == null, "completed (fail=" + fail + ")");
            Check(!bGotSomething, "other client not involved");
            foreach (var f in files) Check(outA.ContainsKey(f.Path) && outA[f.Path].ToArray().SequenceEqual(f.Data), "file intact: " + f.Path);
            Check(!hostCh.Sending, "sender finished");
        });

        Run("file transfer flow control", () =>
        {
            var rig = new Rig();
            var a = rig.Join("A");
            var hostCh = new FileChannel(rig.Host, "wf");
            int chunks = 0;
            a.RegisterMessage("wf.c", (from, r) => chunks++);   // receives but never acknowledges
            var big = new byte[2000000];
            hostCh.Send(a.LocalPeer, new List<FileEntry> { new FileEntry { Path = "x.bin", Data = big } });
            for (int i = 0; i < 50; i++) { hostCh.Tick(); rig.Pump(); }
            Check(chunks * FileChannel.ChunkSize <= hostCh.Window && chunks > 0, "stops at the window without acks: " + chunks + " chunks");
        });

        Run("file transfer rejects unsafe paths", () =>
        {
            Check(!FileChannel.SafePath("../evil") && !FileChannel.SafePath("a/../../b") && !FileChannel.SafePath("/abs") && !FileChannel.SafePath("C:/x") && !FileChannel.SafePath(""), "bad paths refused");
            Check(FileChannel.SafePath("CellsCache/baked-batch-cells-10-grp0.zip") && FileChannel.SafePath("gameinfo.json"), "normal paths allowed");
            var rig = new Rig();
            var a = rig.Join("A");
            var hostCh = new FileChannel(rig.Host, "wf"); var aCh = new FileChannel(a, "wf");
            string fail = null; aCh.ReceiveFailed += w => fail = w;
            aCh.CreateFile = p => new MemoryStream();
            hostCh.Send(a.LocalPeer, new List<FileEntry> { new FileEntry { Path = "../../boot.ini", Data = new byte[3] } });
            for (int i = 0; i < 20; i++) { hostCh.Tick(); rig.Pump(); }
            Check(fail != null && fail.Contains("unsafe"), "receiver refused: " + fail);
        });

        Run("file transfer over tcp", () =>
        {
            var ht = new TcpHostTransport(0);
            var host = SyncSession.CreateHost(ht); host.SendIntervalMs = 0; host.AuditIntervalMs = 0;
            var cl = SyncSession.CreateClient(new TcpClientTransport("127.0.0.1", ht.Port)); cl.SendIntervalMs = 0; cl.AuditIntervalMs = 0;
            for (int i = 0; i < 400 && !cl.Ready; i++) { host.Tick(); cl.Tick(); Thread.Sleep(5); }
            Check(cl.Ready, "joined");
            var hostCh = new FileChannel(host, "wf"); var clCh = new FileChannel(cl, "wf");
            var d = new byte[6000000]; new Random(3).NextBytes(d);
            var ms = new MemoryStream(); bool done = false; string fail = null;
            clCh.CreateFile = p => ms;
            clCh.ReceiveCompleted += () => done = true; clCh.ReceiveFailed += w => fail = w;
            host.RegisterMessage("wf.req", (from, r) => hostCh.Send(from, new List<FileEntry> { new FileEntry { Path = "big.bin", Data = d } }));
            cl.SendMessage("wf.req", new ByteWriter(), 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done && fail == null && sw.ElapsedMilliseconds < 20000) { host.Tick(); hostCh.Tick(); cl.Tick(); Thread.Sleep(1); }
            Check(done && fail == null, "completed in " + sw.ElapsedMilliseconds + " ms (fail=" + fail + ")");
            Check(ms.ToArray().SequenceEqual(d), "bytes intact");
            cl.Dispose(); host.Dispose();
        });


        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "ALL PASS (" + _pass + " checks)" : _fail + " FAILED, " + _pass + " passed");
        return _fail == 0 ? 0 : 1;
    }

    class Bad { [Synced] public object nope; }
    class ThirdParty { public int gold; public int secret; }
}
