using System;

namespace MPCore.Sync
{
    public interface ILog
    {
        void Info(string m);
        void Warn(string m);
        void Error(string m);
    }

    public sealed class NullLog : ILog
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
    }

    public sealed class ConsoleLog : ILog
    {
        public void Info(string m) { Console.WriteLine("[mp] " + m); }
        public void Warn(string m) { Console.WriteLine("[mp WARN] " + m); }
        public void Error(string m) { Console.WriteLine("[mp ERROR] " + m); }
    }

    public enum DesyncKind
    {
        /// <summary>A local mod or game code changed a synced field on a peer that does not own it. Not our bug.</summary>
        LocalOverwrite,
        /// <summary>Owner's value and what we received differ. The network layer lost or mangled an update. OUR bug.</summary>
        LostUpdate,
        /// <summary>A peer sent state for an object it does not own. Cheater, or ownership bookkeeping bug (ours).</summary>
        OwnershipViolation,
        /// <summary>A message we could not decode. Our bug, or mismatched builds.</summary>
        Malformed,
        /// <summary>State arrived for a component that never got bound. Adapter did not bind it.</summary>
        UnboundComponent
    }

    public sealed class DesyncReport
    {
        public DesyncKind Kind;
        public uint NetId;
        public string Component;
        public string Field;
        public string Mod;
        public string Detail;

        public override string ToString()
        {
            return Kind + " obj=" + NetId + " comp=" + Component + " field=" + Field + " mod=" + Mod + " :: " + Detail;
        }
    }

    /// <summary>One difference between the host's and a joiner's mod lists. Null version = mod not installed there.</summary>
    public sealed class ModDiff
    {
        public string ModId;
        public string HostVersion;
        public string YourVersion;
        public string Note;   // e.g. "synced fields differ"
        public override string ToString()
        {
            if (HostVersion == null) return ModId + " " + YourVersion + ": only you have it";
            if (YourVersion == null) return ModId + " " + HostVersion + ": only the host has it";
            if (Note != null) return ModId + ": " + Note;
            return ModId + ": host " + HostVersion + ", you " + YourVersion;
        }
    }

    public sealed class ModInfo
    {
        public string Id;
        public string Version;
        /// <summary>Cosmetic or local-only mod. Not compared in the join handshake.</summary>
        public bool ClientOnly;
    }
}
