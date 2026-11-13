using System.Collections.Generic;
using System.Linq;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Shown when the host's mods differ from the joiner's. Lists every difference and lets the player join anyway.
    /// The multiplayer mod itself must still match; that case is a normal refusal, not this dialog.
    /// </summary>
    sealed class ModCheck : MonoBehaviour
    {
        static ModCheck _inst;
        static List<ModDiff> _diffs;
        public static bool Visible { get { return _diffs != null || GameStyledShowing; } }

        Rect _win = new Rect(0, 0, 640, 10);
        Vector2 _scroll;
        GUIStyle _wrap;

        public static bool GameStyledShowing;

        public static void Show(List<ModDiff> diffs)
        {
            diffs = diffs ?? new List<ModDiff>();
            // game-styled dialog first; the IMGUI window below is the fallback
            try
            {
                var body = "The host's mods are not the same as yours. You can still join, but these mods may behave differently or break for one of you:\n\n"
                    + string.Join("\n", diffs.Select(d => "• " + d).ToArray())
                    + "\n\nMods that change nothing for others (skins, UI) can be listed in the config under Compat/ClientOnlyMods.";
                if (UiDialogs.ShowMessage("Different mods", body,
                        "Join anyway", () => { GameStyledShowing = false; if (Plugin.I != null) Plugin.I.JoinAnyway(); },
                        "Cancel", () => { GameStyledShowing = false; if (Plugin.I != null) Plugin.I.JoinStatus = "Join cancelled: the host has different mods."; }))
                {
                    GameStyledShowing = true;
                    return;
                }
            }
            catch { }
            _diffs = diffs;
            if (_inst == null)
            {
                var go = new GameObject("MP_ModCheck");
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<ModCheck>();
            }
            _inst._win.x = 0; _inst._win.y = 0;
            MainMenuInput.SetMenuClickable(false);
        }

        public static void Hide()
        {
            _diffs = null;
            MainMenuInput.SetMenuClickable(true);
        }

        void OnGUI()
        {
            if (_diffs == null) return;
            if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            float s = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1));
            float W = Screen.width / s, H = Screen.height / s;
            if (_win.x == 0 && _win.y == 0) _win.position = new Vector2((W - _win.width) / 2, H * 0.2f);
            GUI.depth = -1000;
            _win = GUILayout.Window(7110, _win, Draw, "Different mods");
        }

        void Draw(int id)
        {
            GUILayout.Label("The host's mods are not the same as yours. You can still join, but these mods may behave differently or break for one of you:", _wrap);
            GUILayout.Space(6);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Min(260, 26 * _diffs.Count + 10)));
            foreach (var d in _diffs)
            {
                var old = GUI.contentColor;
                GUI.contentColor = d.HostVersion == null || d.YourVersion == null ? new Color(1f, 0.8f, 0.35f) : new Color(1f, 0.6f, 0.5f);
                GUILayout.Label("• " + d, _wrap);
                GUI.contentColor = old;
            }
            GUILayout.EndScrollView();
            GUILayout.Space(8);
            GUILayout.Label("Tip: mods only you have that change nothing for others (skins, UI) can be listed in the config under Compat/ClientOnlyMods.", _wrap);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Join anyway", GUILayout.Height(32)))
            {
                Hide();
                if (Plugin.I != null) Plugin.I.JoinAnyway();
            }
            if (GUILayout.Button("Cancel", GUILayout.Height(32)))
            {
                Hide();
                if (Plugin.I != null) Plugin.I.JoinStatus = "Join cancelled: the host has different mods.";
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }
}
