using HarmonyLib;
using UWE;

namespace SubnauticaMP
{
    /// <summary>
    /// Subnautica freezes game time while the pause menu is open (IngameMenu.OnSelect calls FreezeTime.Begin, OnDeselect
    /// calls End). In multiplayer that would stop the world for everybody, so the freeze is skipped whenever other
    /// players are connected. Alone, or hosting with nobody joined, the pause works as in the stock game.
    /// Only the pause menu's own freeze is touched; loading screens, cutscenes and alt-tab are left alone.
    /// </summary>
    static class FreezeGuard
    {
        static bool _suppressed;   // we skipped the Begin, so the matching End must be skipped too
        static bool _held;         // the freeze is currently active (Begin went through)
        static FreezeTime.Id _heldId;

        static bool IsPauseId(FreezeTime.Id id) { return id == FreezeTime.Id.IngameMenu; }

        static bool OthersConnected { get { return Plugin.I != null && Plugin.I.OthersConnected; } }

        public static bool BeginAllowed(FreezeTime.Id id)
        {
            if (id == FreezeTime.Id.ApplicationFocus && OthersConnected) return false;   // alt-tab must not stop the world for everybody
            if (!IsPauseId(id)) return true;
            if (OthersConnected) { _suppressed = true; return false; }
            _suppressed = false; _held = true; _heldId = id;
            return true;
        }

        public static bool EndAllowed(FreezeTime.Id id)
        {
            if (!IsPauseId(id)) return true;
            if (_suppressed) { _suppressed = false; return false; }
            _held = false;
            return true;
        }

        /// <summary>Someone joined while the menu was already open and frozen: thaw the world now.</summary>
        public static void Tick()
        {
            if (!_held || !OthersConnected) return;
            _held = false;
            FreezeTime.End(_heldId);
            _suppressed = true;   // the menu's own End will arrive later; skip it
        }
    }

    [HarmonyPatch(typeof(FreezeTime), "Begin")]
    static class P_FreezeBegin { static bool Prefix(FreezeTime.Id __0) { return FreezeGuard.BeginAllowed(__0); } }

    [HarmonyPatch(typeof(FreezeTime), "End")]
    static class P_FreezeEnd { static bool Prefix(FreezeTime.Id __0) { return FreezeGuard.EndAllowed(__0); } }
}
