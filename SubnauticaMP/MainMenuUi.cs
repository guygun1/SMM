using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SubnauticaMP
{
    sealed class UiTicker : MonoBehaviour
    {
        public Action OnTick;
        void Update() { if (OnTick != null) { try { OnTick(); } catch { } } }
    }

    /// <summary>
    /// Adds a Join button and a host toggle to the top right of the game's own Play screen, and a join dialog built
    /// from the game's own fonts and button sprites. Everything is created at runtime on the loaded menu.
    /// If anything throws, Broken is set and the plain overlay is used instead.
    /// </summary>
    static class MainMenuUi
    {
        public static bool Broken;
        const string Tag = "MP_PlayHeader";

        // Text boxes are drawn with Unity's own text field (always works, no matter how the menu handles input),
        // placed exactly over a backdrop box that is part of the game-styled dialog.
        internal sealed class Field { public RectTransform Rt; public Func<string> Get; public Action<string> Set; public Action Enter; public string Ctrl, Hint; public int Max = 80; public bool Focus = true, Logged; }
        internal static readonly System.Collections.Generic.List<Field> Fields = new System.Collections.Generic.List<Field>();
        static GUIStyle _fieldStyle;

        // In a running world (the pause menu is open over the game) Unity's text field does not get the keyboard: the game's own input
        // handling takes it. There the typed characters are read straight from Input.inputString (CaptureKeys, every frame) and the box
        // shows the text with a blinking bar. Tab moves to the next box, Enter confirms the dialog's action.
        internal static bool RawMode { get { return Player.main != null && !MenuState.MainMenuVisible; } }
        static int _active;

        internal static void CaptureKeys()
        {
            if (!RawMode || Fields.Count == 0) return;
            Fields.RemoveAll(f => f.Rt == null);
            if (Fields.Count == 0) return;
            if (_active >= Fields.Count) _active = 0;
            var f = Fields[_active];
            string text = f.Get() ?? "";
            bool changed = false;
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (text.Length > 0) { text = text.Substring(0, text.Length - 1); changed = true; } }
                else if (c == '\t') { _active = (_active + 1) % Fields.Count; return; }
                else if (c == '\n' || c == '\r') { if (changed) f.Set(text); if (f.Enter != null) f.Enter(); return; }
                else if (!char.IsControl(c) && text.Length < f.Max) { text += c; changed = true; }
            }
            if (changed) f.Set(text);
        }

        static void DrawRaw(Field f, Rect r, int index)
        {
            bool active = index == _active;
            string text = f.Get() ?? "";
            if (active && ((int)(Time.unscaledTime * 2f) & 1) == 0) text += "|";
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(f.Hint)) { var old = GUI.color; GUI.color = new Color(1, 1, 1, 0.4f); GUI.Label(r, f.Hint, _fieldStyle); GUI.color = old; }
            else GUI.Label(r, text, _fieldStyle);
            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition)) { _active = index; Event.current.Use(); }   // click a box to type in it
        }

        public static void DrawFields(float scale)
        {
            if (Fields.Count == 0) return;
            if (_fieldStyle == null)
            {
                _fieldStyle = new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                var clear = new Texture2D(1, 1); clear.SetPixel(0, 0, new Color(0, 0, 0, 0)); clear.Apply();
                _fieldStyle.normal.background = clear; _fieldStyle.focused.background = clear; _fieldStyle.hover.background = clear; _fieldStyle.active.background = clear;
                _fieldStyle.normal.textColor = _fieldStyle.focused.textColor = _fieldStyle.hover.textColor = _fieldStyle.active.textColor = Color.white;
                _fieldStyle.padding = new RectOffset(14, 14, 0, 0);
                try
                {
                    // the game's own font where Unity exposes it as a normal font (dynamic TMP font assets do)
                    var tf = UiDialogs.FontAsset != null ? Traverse.Create(UiDialogs.FontAsset).Property("sourceFontFile").GetValue<Font>() : null;
                    if (tf != null) _fieldStyle.font = tf;
                }
                catch { }
            }
            Fields.RemoveAll(f => f.Rt == null);
            foreach (var f in Fields.ToArray())
            {
                if (f.Rt == null) continue;
                var c = new Vector3[4];
                f.Rt.GetWorldCorners(c);
                var canvas = f.Rt.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
                Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, c[0]), tr = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                var r = new Rect(bl.x / scale, (Screen.height - tr.y) / scale, (tr.x - bl.x) / scale, (tr.y - bl.y) / scale);
                if (r.width < 20 || r.height < 10) { if (!f.Logged) { f.Logged = true; if (Plugin.I != null) Plugin.I.Log.Warn("text field " + f.Ctrl + " has no size yet: " + r); } continue; }
                _fieldStyle.fontSize = Mathf.Max(12, (int)(r.height * 0.5f));
                if (RawMode) { DrawRaw(f, r, Fields.IndexOf(f)); continue; }
                GUI.SetNextControlName(f.Ctrl);
                if (!f.Logged) { f.Logged = true; if (Plugin.I != null) Plugin.I.Log.Info("text field " + f.Ctrl + " drawn at " + r); }
                var ev = Event.current;
                if (f.Enter != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == f.Ctrl)
                {
                    ev.Use();
                    f.Enter();
                    continue;
                }
                var now = GUI.TextField(r, f.Get() ?? "", f.Max, _fieldStyle);
                if (string.IsNullOrEmpty(now) && !string.IsNullOrEmpty(f.Hint))
                {
                    var old = GUI.color; GUI.color = new Color(1, 1, 1, 0.4f);
                    GUI.Label(r, f.Hint, _fieldStyle);
                    GUI.color = old;
                }
                if (now != f.Get()) f.Set(now);
                if (f.Focus) { GUI.FocusControl(f.Ctrl); f.Focus = false; }
            }
        }

        /// <summary>The visible part of a text box: a dark inset with a thin light edge. The IMGUI text field is drawn over it.</summary>
        internal static GameObject FieldBox(Transform parent, string ctrl, Func<string> get, Action<string> set, string hint, int max, Action enter)
        {
            var go = new GameObject("MP_FieldBox_" + ctrl, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = UiDialogs.RoundRect(); img.type = Image.Type.Sliced;
            img.color = new Color(0.01f, 0.05f, 0.14f, 0.92f);
            var edge = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            edge.transform.SetParent(go.transform, false);
            Stretch((RectTransform)edge.transform, 0, 0);
            var eimg = edge.GetComponent<Image>(); eimg.sprite = UiDialogs.RoundRectLine(); eimg.type = Image.Type.Sliced; eimg.raycastTarget = false;
            eimg.color = new Color(0.55f, 0.82f, 1f, 0.55f);
            var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = 1; le.minHeight = 56; le.preferredHeight = 56;
            Fields.Add(new Field { Rt = (RectTransform)go.transform, Get = get, Set = set, Ctrl = ctrl, Hint = hint, Max = max, Enter = enter });
            return go;
        }

        // ------------------------------------------------------------------ Play screen header

        public static void Install(MainMenuLoadPanel panel)
        {
            var p = Plugin.I;
            if (p == null || panel == null || Broken) return;
            try
            {
                TMP_Text header; Transform root;
                if (!FindHeader(panel, out header, out root)) { p.Log.Warn("Play screen: header not found, using overlay toggle"); Broken = true; return; }

                // the blue panel itself (the title sits inside it); the toggle is a child so it moves with the panel's animation
                Transform vis = header.transform.parent;
                for (var t = header.transform.parent; t != null; t = t.parent)
                {
                    var rt0 = t as RectTransform;
                    if (rt0 != null && t.GetComponent<Image>() != null && rt0.rect.width >= 400f) { vis = t; break; }
                }
                if (vis.Find(Tag) != null) return;   // already there
                if (p.DumpMenu) p.Log.Info("Play screen hierarchy (" + vis.name + "):\n" + UiClone.Dump(vis, 0, 5));

                float fs = Mathf.Clamp(header.fontSize * 0.42f, 17f, 26f);   // small: a switch, not a second title
                var tg = MakeToggle(vis, "Host for friends", header, fs, () => p.HostNextGame, v => p.SetHosting(v));
                tg.name = Tag;
                var le = tg.AddComponent<LayoutElement>(); le.ignoreLayout = true;
                var vrt = (RectTransform)vis;
                var trt = (RectTransform)tg.transform;
                var tick = tg.AddComponent<UiTicker>();
                var prev = tick.OnTick;
                tick.OnTick = () =>
                {
                    var c = vrt.InverseTransformPoint(header.rectTransform.TransformPoint(header.rectTransform.rect.center));
                    if (float.IsNaN(c.y) || float.IsInfinity(c.y)) return;
                    trt.localPosition = new Vector3(vrt.rect.xMax - 36f, c.y, 0f);
                };
                _toggleTick = tick;
                p.Log.Info("added host toggle to the Play screen");
            }
            catch (Exception e)
            {
                Broken = true;
                p.Log.Error("Play screen toggle failed, using overlay instead: " + e);
            }
        }
        static UiTicker _toggleTick;

        // ------------------------------------------------------------------ main menu entry "Join" (above Play)

        public static void InstallMenuEntry()
        {
            var p = Plugin.I;
            var menu = uGUI_MainMenu.main;
            if (p == null || menu == null || Broken) return;
            try
            {
                var opts = menu.primaryOptions;
                if (opts == null) return;
                foreach (var t in opts.GetComponentsInChildren<Transform>(true)) if (t.name == "MP_JoinEntry") return;
                p.Log.Info("main menu options:\n" + UiClone.Dump(opts.transform, 0, 4));

                // the entries are plain Buttons inside a vertical layout (ButtonPlay, ButtonOptions, ...)
                Button play = null;
                foreach (var bt in opts.GetComponentsInChildren<Button>(true))
                    for (int i = 0; i < bt.onClick.GetPersistentEventCount(); i++)
                        if (bt.onClick.GetPersistentMethodName(i) == "OnButtonLoad") play = bt;
                if (play == null) play = opts.GetComponentsInChildren<Button>(true).FirstOrDefault(bt => bt.name == "ButtonPlay");
                if (play == null) { p.Log.Warn("main menu: no Play button found to copy"); return; }

                var go = UnityEngine.Object.Instantiate(play.gameObject, play.transform.parent);
                go.name = "MP_JoinEntry";
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var n = mb.GetType().Name;
                    if (n == "LanguageUpdater" || n == "LocalizeTextMesh" || n == "TranslationLiveUpdate") UnityEngine.Object.DestroyImmediate(mb);
                }
                // keep hover/highlight reactions of the EventTrigger, drop its click/submit actions
                // (sounds and highlights stay; only entries that call one of the menu's own OnButton... actions go)
                foreach (var et in go.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true))
                    et.triggers.RemoveAll(e =>
                    {
                        if (e.eventID != UnityEngine.EventSystems.EventTriggerType.PointerClick && e.eventID != UnityEngine.EventSystems.EventTriggerType.Submit) return false;
                        bool menuAction = false;
                        for (int i = 0; i < e.callback.GetPersistentEventCount(); i++)
                        {
                            var mn = e.callback.GetPersistentMethodName(i) ?? "";
                            p.Log.Info("main menu Join entry: trigger " + e.eventID + " calls " + mn);
                            if (mn.StartsWith("OnButton")) menuAction = true;
                        }
                        return menuAction || e.callback.GetPersistentEventCount() == 0;
                    });
                TMP_Text style = null;
                foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) { t.text = "Join"; if (style == null) style = t; }
                if (style == null) { UnityEngine.Object.Destroy(go); p.Log.Warn("main menu: Play button has no text"); return; }
                UiDialogs.SetStyle(style, null, null);
                foreach (var b in go.GetComponentsInChildren<Button>(true))
                {
                    b.onClick = new Button.ButtonClickedEvent();
                    b.onClick.AddListener(() =>
                    {
                        try { if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null); } catch { }   // no stuck highlight behind the dialog
                        p.OpenJoinPanel();
                    });
                }
                go.transform.SetParent(play.transform.parent, false);
                go.transform.SetSiblingIndex(play.transform.GetSiblingIndex());   // the layout puts it above Play
                go.SetActive(true);
                p.Log.Info("added Join entry to the main menu (copied '" + play.name + "')");
            }
            catch (Exception e)
            {
                Broken = true;
                p.Log.Error("main menu Join entry failed, using overlay instead: " + e);
            }
        }

        // ------------------------------------------------------------------ a real switch

        static Sprite _pill, _circle;

        static Sprite Pill()
        {
            if (_pill != null) return _pill;
            const int W = 64, H = 32; const float R = 16f;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, R, W - R), cy = H * 0.5f;
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(R - d + 0.5f)));
                }
            tex.Apply();
            _pill = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
            return _pill;
        }

        public static Sprite CircleSprite() { return Circle(); }

        static Sprite Circle()
        {
            if (_circle != null) return _circle;
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - S / 2f) * (x + 0.5f - S / 2f) + (y + 0.5f - S / 2f) * (y + 0.5f - S / 2f));
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(S / 2f - d + 0.5f)));
                }
            tex.Apply();
            _circle = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            return _circle;
        }

        /// <summary>Label on the left, small sliding switch on the right. Click anywhere on it to flip.</summary>
        static GameObject MakeToggle(Transform parent, string label, TMP_Text style, float fontSize, Func<bool> get, Action<bool> set)
        {
            float tw = Mathf.Round(fontSize * 2.3f), th = Mathf.Round(fontSize * 1.25f), kn = th - 6f;
            float xOff = 3f + kn * 0.5f, xOn = tw - 3f - kn * 0.5f;
            var go = new GameObject("MP_Toggle", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(Mathf.Round(fontSize * 8.2f + tw + 12f), Mathf.Round(th + 8f));
            var bg = go.GetComponent<Image>(); bg.color = new Color(0, 0, 0, 0.001f);   // invisible, but catches clicks
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => set(!get()));

            var txt = MakeText(go.transform, "Label", style, fontSize, label, TextAlignmentOptions.MidlineRight);
            var trt = (RectTransform)txt.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = new Vector2(-(tw + 12f), 0);
            txt.enableWordWrapping = false;
            txt.overflowMode = TextOverflowModes.Overflow;
            var baseCol = txt.color;

            var track = new GameObject("Track", typeof(RectTransform), typeof(Image));
            track.transform.SetParent(go.transform, false);
            var tr = (RectTransform)track.transform;
            tr.anchorMin = tr.anchorMax = new Vector2(1f, 0.5f); tr.pivot = new Vector2(1f, 0.5f);
            tr.sizeDelta = new Vector2(tw, th); tr.anchoredPosition = new Vector2(-2f, 0f);
            var timg = track.GetComponent<Image>(); timg.sprite = Pill(); timg.type = Image.Type.Sliced; timg.raycastTarget = false;

            var knob = new GameObject("Knob", typeof(RectTransform), typeof(Image));
            knob.transform.SetParent(track.transform, false);
            var kr = (RectTransform)knob.transform;
            kr.anchorMin = kr.anchorMax = new Vector2(0f, 0.5f); kr.pivot = new Vector2(0.5f, 0.5f);
            kr.sizeDelta = new Vector2(kn, kn);
            var kimg = knob.GetComponent<Image>(); kimg.sprite = Circle(); kimg.raycastTarget = false;

            float pos = get() ? xOn : xOff;
            var tick = go.AddComponent<UiTicker>();
            tick.OnTick = () =>
            {
                bool on = get();
                float target = on ? xOn : xOff;
                pos = Mathf.Lerp(pos, target, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
                kr.anchoredPosition = new Vector2(pos, 0f);
                float k = Mathf.InverseLerp(xOff, xOn, pos);
                timg.color = Color.Lerp(new Color(0.08f, 0.2f, 0.38f, 0.95f), new Color(0.2f, 0.72f, 0.5f, 1f), k);
                kimg.color = Color.Lerp(new Color(0.72f, 0.82f, 0.92f, 1f), Color.white, k);
                txt.color = Color.Lerp(new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * 0.7f), baseCol, k);
            };
            return go;
        }

        /// <summary>The "Play" title: the top-most text on the Play screen that is not part of a save slot.</summary>
        static bool FindHeader(MainMenuLoadPanel panel, out TMP_Text header, out Transform root)
        {
            header = null; root = null;
            TMP_Text fallback = null;
            var anc = panel.transform;
            for (int level = 0; level < 6 && anc != null; level++, anc = anc.parent)
            {
                var texts = anc.GetComponentsInChildren<TMP_Text>(true)
                    .Where(t => t.GetComponentInParent<MainMenuLoadButton>() == null && !string.IsNullOrEmpty(t.text) && t.text.Length <= 24).ToList();
                if (texts.Count == 0) continue;
                var exact = texts.FirstOrDefault(t => string.Equals(t.text.Trim(), "Play", StringComparison.OrdinalIgnoreCase));
                if (exact != null) { header = exact; break; }
                if (fallback == null) fallback = texts.OrderByDescending(t => t.transform.position.y).First();
            }
            if (header == null) header = fallback;
            if (header == null) return false;
            // the panel that holds both the title and the list
            var r = header.transform.parent;
            while (r != null && !panel.transform.IsChildOf(r)) r = r.parent;
            root = r ?? header.transform.parent;
            return root != null;
        }

        static void Sprites(MainMenuLoadPanel panel, out Sprite normal, out Sprite hover)
        {
            normal = hover = null;
            try
            {
                var menu = panel.GetComponentInParent<MainMenuLoadMenu>() ?? UnityEngine.Object.FindObjectOfType<MainMenuLoadMenu>();
                if (menu == null) return;
                var t = Traverse.Create(menu);
                normal = t.Field("normalSprite").GetValue<Sprite>();
                hover = t.Field("selectedSprite").GetValue<Sprite>();
            }
            catch { }
        }

        static void SetRight(GameObject b, float xRight, float y)
        {
            var rt = (RectTransform)b.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.localPosition = new Vector3(xRight, y, 0f);
        }

        // ------------------------------------------------------------------ widgets built from the game's own look

        public static GameObject MakeButton(Transform parent, string label, TMP_Text style, float fontSize, Sprite normal, Sprite hover, float w, float h, UnityAction onClick)
        {
            var go = new GameObject("MP_Button_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
            var img = go.GetComponent<Image>();
            if (normal != null)
            {
                img.sprite = normal;
                img.type = normal.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
                img.color = Color.white;
            }
            else img.color = new Color(0.05f, 0.35f, 0.75f, 0.9f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            if (hover != null && normal != null)
            {
                btn.transition = Selectable.Transition.SpriteSwap;
                var ss = btn.spriteState; ss.highlightedSprite = hover; ss.selectedSprite = hover; ss.pressedSprite = hover; btn.spriteState = ss;
            }
            else
            {
                btn.transition = Selectable.Transition.ColorTint;
                var cb = btn.colors; cb.highlightedColor = new Color(0.8f, 0.95f, 1f); cb.pressedColor = new Color(0.6f, 0.8f, 1f); btn.colors = cb;
            }
            btn.onClick.AddListener(onClick);

            var t = MakeText(go.transform, "Label", style, fontSize, label, TextAlignmentOptions.Center);
            Stretch((RectTransform)t.transform, 8, 4);
            return go;
        }

        public static TextMeshProUGUI MakeText(Transform parent, string name, TMP_Text style, float fontSize, string value, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (style != null)
            {
                t.font = style.font;
                t.fontSharedMaterial = style.fontSharedMaterial;
                t.color = style.color;
            }
            t.fontSize = fontSize;
            t.alignment = align;
            t.enableWordWrapping = true;
            t.richText = false;
            t.raycastTarget = false;
            t.text = value;
            return t;
        }

        static void Stretch(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY); rt.offsetMax = new Vector2(-padX, -padY);
        }

        // ------------------------------------------------------------------ dialogs (see UiDialogs.cs)

        /// <summary>Closes whichever game-styled dialog is open. Safe to call at any time.</summary>
        public static void CloseDialog()
        {
            Fields.Clear();
            UiDialogs.Destroy();
        }
    }

    [HarmonyPatch(typeof(MainMenuLoadPanel), "OnEnable")]
    static class P_PlayPanelEnable
    {
        static void Postfix(MainMenuLoadPanel __instance)
        {
            var p = Plugin.I;
            if (p == null) return;
            var inst = __instance;
            p.Defer(() => MainMenuUi.Install(inst));   // next frame: the screen's own texts are set by then
        }
    }
}
