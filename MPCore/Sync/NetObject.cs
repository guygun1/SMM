using System.Collections.Generic;

namespace MPCore.Sync
{
    /// <summary>One synced component instance on a network object.</summary>
    public sealed class Tracked
    {
        public uint Key;
        public string KeyName;
        public object Target;
        public SyncType Type;
        /// <summary>Last encoded value of each field that was sent (owner) or received (everyone else). null = never synced.</summary>
        internal byte[][] Last;
    }

    public sealed class NetObject
    {
        /// <summary>Layout: bit31 = scene object (hash of path). Else (peerId &lt;&lt; 24) | counter, so peers never collide.</summary>
        public uint Id;
        public int Owner;
        public string Prefab;
        public byte[] SpawnData;
        public bool IsScene;
        public readonly Dictionary<uint, Tracked> Components = new Dictionary<uint, Tracked>();
        /// <summary>Free slot for the game adapter (e.g. the GameObject).</summary>
        public object UserData;
    }

    internal enum Msg : byte
    {
        Hello = 1, Welcome = 2, Reject = 3, Spawn = 4, Adopt = 5, Despawn = 6, State = 7,
        OwnerRequest = 8, OwnerChange = 9, Custom = 10, PeerJoined = 11, PeerLeft = 12,
        Audit = 13, SnapshotDone = 14, Resync = 15, ModsDiffer = 16
    }

    internal sealed class PeerInfo
    {
        public int Id;
        public string Name;
        public bool Ready;
    }
}
