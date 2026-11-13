using System;
using System.Collections.Generic;
using System.Threading;
using MPCore.Net;
using MPCore.Sync;

namespace SubnauticaMP
{
    /// <summary>
    /// Everything that makes a world joinable: TCP by IP, Steam relay, UPnP, and the "do I still need to port forward" check.
    /// One MultiHostTransport carries all of them, so players on different transports share one world.
    /// </summary>
    public sealed class HostService : IDisposable
    {
        public SyncSession Session;
        public ReachabilityProbe Probe;
        public int Port;
        public bool TcpOk, SteamOk;
        public string TcpError;

        public static HostService Start(int port, bool useSteam, MPCore.Sync.ILog log)
        {
            var h = new HostService { Port = port };
            var transports = new List<ITransport>();
            bool advertised = false;
            SyncSession session = null;

            try
            {
                try { transports.Add(new TcpHostTransport(port)); h.TcpOk = true; }
                catch (Exception e) { h.TcpError = e.Message; log.Warn("TCP listen on " + port + " failed: " + e.Message); }

                if (useSteam && SteamSupport.Available)
                {
                    try { transports.Add(new SteamHostTransport()); h.SteamOk = true; SteamSupport.AdvertiseHosting(true); advertised = true; }
                    catch (Exception e) { log.Warn("Steam host failed: " + e.Message); }
                }

                if (transports.Count == 0) throw new InvalidOperationException("nothing could start (" + h.TcpError + ")");

                session = SyncSession.CreateHost(new MultiHostTransport(transports.ToArray()));
                h.Session = session;
                if (h.TcpOk)
                {
                    h.Probe = new ReachabilityProbe();
                    h.Probe.Start(port, log.Info);
                }
                return h;
            }
            catch
            {
                // Undo what was created so far: nothing may keep listening or advertising after a failed start.
                if (session != null) { try { session.Dispose(); } catch { } }   // also disposes the transports it owns
                else foreach (var t in transports) { try { t.Dispose(); } catch { } }
                if (advertised) { try { SteamSupport.AdvertiseHosting(false); } catch { } }
                if (h.Probe != null) DisposeOffThread(h.Probe);
                throw;
            }
        }

        /// <summary>UPnP's delete-mapping is a network call, so it must not block the game's main thread.</summary>
        static void DisposeOffThread(IDisposable d)
        {
            ThreadPool.QueueUserWorkItem(_ => { try { d.Dispose(); } catch { } });
        }

        public ReachabilityReport Report { get { return Probe != null ? Probe.Report : null; } }

        /// <summary>The address to give a friend over the internet, or null when only Steam join / LAN is possible.</summary>
        public string InternetAddress { get { var r = Report; return r != null ? r.JoinAddress : null; } }
        public string LanAddress { get { var r = Report; return r != null ? r.LanAddress : null; } }

        public void Dispose()
        {
            if (SteamOk) SteamSupport.AdvertiseHosting(false);
            if (Probe != null) DisposeOffThread(Probe);
            if (Session != null) Session.Dispose();
        }
    }
}
