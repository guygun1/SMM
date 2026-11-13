using System;
using BepInEx.Configuration;
using MPCore.IO;

namespace SubnauticaMP
{
    /// <summary>
    /// Game rules the host decides for everybody (shared PDA, radio, blueprints, friendly fire...).
    /// The host's config values are sent to every joiner when they join and whenever they change; joiners use the
    /// host's values, never their own. Features check these flags before sending or applying.
    /// </summary>
    public sealed class HostSettings : ISyncFeature
    {
        public string Name { get { return "host-settings"; } }

        // current values (the host's own config on the host, the received values on a joiner)
        public static bool SharedBlueprints = true;
        public static bool SharedPda = true;          // encyclopedia entries and scan progress
        public static bool SharedRadio = true;        // radio messages and their story goals
        public static bool SharedStory = true;        // all other story goals
        public static bool FriendlyFire = true;       // players can hurt each other
        public static bool SharedTimeOfDay = true;
        public static bool SharedPower = true;

        static ConfigEntry<bool> _bp, _pda, _radio, _story, _ff, _time, _power;
        static bool _bound;
        float _nextCheck;
        string _lastSent;

        /// <summary>Bind the config entries (host values). Safe to call more than once.</summary>
        public static void Bind(ConfigFile cfg)
        {
            if (_bound || cfg == null) return;
            _bound = true;
            _bp = cfg.Bind("HostSettings", "SharedBlueprints", true, "Host: a blueprint unlocked by one player is unlocked for everyone.");
            _pda = cfg.Bind("HostSettings", "SharedPDA", true, "Host: PDA entries and scan progress are shared.");
            _radio = cfg.Bind("HostSettings", "SharedRadioMessages", true, "Host: radio messages play for everyone.");
            _story = cfg.Bind("HostSettings", "SharedStory", true, "Host: story progress (goals, events) is shared.");
            _ff = cfg.Bind("HostSettings", "FriendlyFire", true, "Host: players can hurt each other.");
            _time = cfg.Bind("HostSettings", "SharedTimeOfDay", true, "Host: everyone has the host's time of day.");
            _power = cfg.Bind("HostSettings", "SharedPower", true, "Host: power levels of bases and the Cyclops follow the host.");
            LoadLocal();
        }

        static void LoadLocal()
        {
            if (!_bound) return;
            SharedBlueprints = _bp.Value; SharedPda = _pda.Value; SharedRadio = _radio.Value; SharedStory = _story.Value;
            FriendlyFire = _ff.Value; SharedTimeOfDay = _time.Value; SharedPower = _power.Value;
        }

        static string Pack()
        {
            return (SharedBlueprints ? "1" : "0") + (SharedPda ? "1" : "0") + (SharedRadio ? "1" : "0") + (SharedStory ? "1" : "0")
                 + (FriendlyFire ? "1" : "0") + (SharedTimeOfDay ? "1" : "0") + (SharedPower ? "1" : "0");
        }

        static void Unpack(string s)
        {
            if (s == null || s.Length < 7) return;
            SharedBlueprints = s[0] == '1'; SharedPda = s[1] == '1'; SharedRadio = s[2] == '1'; SharedStory = s[3] == '1';
            FriendlyFire = s[4] == '1'; SharedTimeOfDay = s[5] == '1'; SharedPower = s[6] == '1';
        }

        public void Register()
        {
            if (Plugin.I != null) Bind(Plugin.I.Config);
            LoadLocal();
            _lastSent = null;
            SyncKit.On("hs.set", (from, r) =>
            {
                if (from != 0) return;   // only the host decides
                var s = r.Str();
                Unpack(s);
                SyncKit.Info("host settings: " + Describe());
            });
            // a new joiner asks; the host answers
            SyncKit.On("hs.ask", (from, r) => { if (SyncKit.IsHost) SendTo(from); });
            if (!SyncKit.IsHost) SyncKit.SendTo(0, "hs.ask", null);
        }

        public static string Describe()
        {
            return "blueprints " + On(SharedBlueprints) + ", PDA " + On(SharedPda) + ", radio " + On(SharedRadio) + ", story " + On(SharedStory)
                 + ", friendly fire " + On(FriendlyFire) + ", time " + On(SharedTimeOfDay) + ", power " + On(SharedPower);
        }

        static string On(bool b) { return b ? "shared" : "own"; }

        static void SendTo(int peer)
        {
            var p = Pack();
            SyncKit.SendTo(peer, "hs.set", w => w.Str(p));
        }

        public void Tick()
        {
            if (!SyncKit.IsHost || UnityEngine.Time.unscaledTime < _nextCheck) return;
            _nextCheck = UnityEngine.Time.unscaledTime + 1f;
            LoadLocal();                 // the host may change the config while playing
            var p = Pack();
            if (p == _lastSent || !SyncKit.Active) return;
            _lastSent = p;
            SyncKit.Send("hs.set", w => w.Str(p));
        }
    }
}
