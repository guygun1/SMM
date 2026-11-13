using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SubnauticaMP
{
    /// <summary>
    /// All menu changes are made at runtime with Harmony patches and by cloning existing buttons in memory.
    /// No game file is touched, so removing the plugin restores the stock menus.
    /// </summary>
    static class MenuState
    {
        public static bool PauseOpen;
        public static bool MainMenuVisible { get { return uGUI_MainMenu.main != null && Player.main == null; } }
    }

    static class UiClone
    {
        /// <summary>Copies an existing menu button (look, sound, layout slot) and gives it our label and click action.</summary>
        public static GameObject CloneButton(Button template, string label, UnityAction onClick)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            go.name = "MP_" + label.Replace(' ', '_');
            // the game re-translates text from keys on enable/language change; drop those so our label stays ours
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var n = mb.GetType().Name;
                if (n == "LanguageUpdater" || n == "LocalizeTextMesh") UnityEngine.Object.DestroyImmediate(mb);
            }
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent(); // also removes the original's inspector-wired action
            b.onClick.AddListener(onClick);
            foreach (var t in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) t.text = label;
            foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = label;
            go.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            go.SetActive(true);
            return go;
        }

        /// <summary>If the parent has no layout group, buttons will overlap: put ours under the lowest sibling instead.</summary>
        public static void PlaceBelowIfNoLayout(GameObject clone, Transform parent)
        {
            if (parent.GetComponent<LayoutGroup>() != null) return;
            var rt = clone.transform as RectTransform;
            if (rt == null) return;
            float lowest = float.MaxValue;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i) as RectTransform;
                if (c == null || c == rt || !c.gameObject.activeSelf) continue;
                lowest = Mathf.Min(lowest, c.anchoredPosition.y);
            }
            if (lowest < float.MaxValue) rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, lowest - rt.rect.height * 1.15f);
        }

        public static string Dump(Transform t, int depth = 0, int maxDepth = 4)
        {
            var sb = new StringBuilder();
            Append(sb, t, depth, maxDepth);
            return sb.ToString();
        }

        static void Append(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            sb.Append(' ', depth * 2).Append(t.name);
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(' ').Append(c.GetType().Name);
            sb.AppendLine();
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) Append(sb, t.GetChild(i), depth + 1, maxDepth);
        }
    }

    [HarmonyPatch(typeof(uGUI_MainMenu), "Start")]
    static class P_MainMenuStart
    {
        static void Postfix(uGUI_MainMenu __instance)
        {
            var p = Plugin.I;
            if (p == null) return;
            p.Defer(MainMenuUi.InstallMenuEntry);   // next frame: the menu's own entries exist by then
        }
    }

    [HarmonyPatch(typeof(IngameMenu), "Start")]
    static class P_PauseMenuStart
    {
        static void Postfix(IngameMenu __instance)
        {
            var p = Plugin.I;
            if (p == null) return;
            try
            {
                if (p.DumpMenu) p.Log.Info("pause menu hierarchy:\n" + UiClone.Dump(__instance.mainPanel.transform));
                var tpl = __instance.helpButton;
                if (tpl == null) return;
                var go = UiClone.CloneButton(tpl, "Multiplayer", () => p.OpenMultiplayer());
                UiClone.PlaceBelowIfNoLayout(go, tpl.transform.parent);
                p.Log.Info("added Multiplayer button to the pause menu");
            }
            catch (Exception e) { p.Log.Error("pause menu button failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(IngameMenu), "Open")]
    static class P_PauseOpen { static void Postfix() { MenuState.PauseOpen = true; } }

    [HarmonyPatch(typeof(IngameMenu), "Close")]
    static class P_PauseClose
    {
        static void Postfix()
        {
            MenuState.PauseOpen = false;
            if (Plugin.I != null) Plugin.I.PausePanelOpen = false;
        }
    }

    static class MainMenuInput
    {
        static readonly FieldInfo Raycaster = AccessTools.Field(typeof(uGUI_MainMenu), "graphicRaycaster");

        /// <summary>IMGUI windows do not stop clicks reaching the uGUI menu behind them, so switch its raycaster off while ours is open.</summary>
        public static void SetMenuClickable(bool on)
        {
            try
            {
                if (uGUI_MainMenu.main == null || Raycaster == null) return;
                var rc = Raycaster.GetValue(uGUI_MainMenu.main) as Behaviour;
                if (rc != null) rc.enabled = on;
            }
            catch { }
        }
    }
}
