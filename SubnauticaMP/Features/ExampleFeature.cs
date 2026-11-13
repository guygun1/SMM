using HarmonyLib;
using MPCore.IO;

namespace SubnauticaMP.Features
{
    /// <summary>Smallest possible feature: shows the shape every feature file follows. See docs/SYNC_API.md.</summary>
    sealed class ExampleFeature : ISyncFeature
    {
        public string Name { get { return "example"; } }

        public void Register()
        {
            SyncKit.On("example.ping", (from, r) => SyncKit.Info("example: ping from peer " + from + ": " + r.Str()));
        }

        public void Tick() { }
    }
}
