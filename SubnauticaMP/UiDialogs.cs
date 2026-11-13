using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using MPCore.Net;
using UnityEngine.UI;

namespace SubnauticaMP
{
    /// <summary>
    /// The Join and "Choose your player" dialogs, built from the game's own look: its TextMeshPro font, its blue
    /// panel sprites (MainMenuLoadMenu.normalSprite / selectedSprite) and its button behaviour.
    ///
    /// They live on a canvas of their own (Screen Space Overlay, sorted above everything), not under the menu's root
    /// canvas. The menu canvas is a camera/world-scaled canvas, and a dialog parented there was invisible.
    /// Text boxes are Unity IMGUI text fields drawn over a uGUI backdrop (see MainMenuUi.DrawFields), because a
    /// TMP_InputField did not take input on the menu.
    ///
    /// If building throws, Failed is set and Gui falls back to the plain IMGUI windows.
    /// </summary>
    static class UiDialogs
    {
        public static bool Failed;
        static GameObject _canvasGo;
        static string _kind;
        static Action _onEsc;
        static readonly List<Texture2D> _tex = new List<Texture2D>();

        // ------------------------------------------------------------------ the game's look

        static TMP_FontAsset _font; static Material _fontMat; static Color _textColor = Color.white;
        static Sprite _normal, _hover;

        public static TMP_FontAsset FontAsset { get { return _font; } }

        static readonly Color White = new Color(0.96f, 0.98f, 1f, 1f);
        static readonly Color Section = new Color(0.55f, 0.82f, 1f, 1f);
        static readonly Color Dim = new Color(0.72f, 0.82f, 0.92f, 0.9f);
        static readonly Color Good = new Color(0.45f, 1f, 0.55f, 1f);
        static readonly Color Warn = new Color(1f, 0.8f, 0.3f, 1f);
        static readonly Color Bad = new Color(1f, 0.55f, 0.45f, 1f);

        /// <summary>Called whenever a real menu text is at hand (the Join entry copies the Play button's).</summary>
        public static void SetStyle(TMP_Text style, Sprite normal, Sprite hover)
        {
            try
            {
                if (style != null && style.font != null)
                {
                    _font = style.font; _fontMat = style.fontSharedMaterial;
                    var c = style.color; c.a = 1f;
                    _textColor = c.maxColorComponent > 0.6f ? c : Color.white;
                }
                if (normal != null) { _normal = normal; _hover = hover; }
            }
            catch { }
        }

        static void ResolveTheme()
        {
            try
            {
                if (_font == null)
                {
                    TMP_Text t = null;
                    var menu = uGUI_MainMenu.main;
                    if (menu != null && menu.primaryOptions != null) t = menu.primaryOptions.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(x => x.font != null);
                    if (t == null) t = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(x => x.font != null && x.gameObject.scene.IsValid());
                    SetStyle(t, null, null);
                }
                if (_normal == null)
                {
                    var lm = Resources.FindObjectsOfTypeAll<MainMenuLoadMenu>().FirstOrDefault();
                    if (lm != null) { _normal = lm.normalSprite; _hover = lm.selectedSprite; }
                }
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("dialog theme: " + e.Message); }
        }

        // ------------------------------------------------------------------ generated sprites (rounded corners), used where the game has none at hand

        static Sprite _rr, _rrLine;

        static Sprite MakeRound(bool line)
        {
            const int S = 48; const float R = 14f;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float qx = Mathf.Abs(px - S / 2f) - (S / 2f - R), qy = Mathf.Abs(py - S / 2f) - (S / 2f - R);
                    float d = Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0)) + Mathf.Min(Mathf.Max(qx, qy), 0) - R;   // < 0 inside
                    float a = Mathf.Clamp01(0.5f - d);
                    if (line) a = Mathf.Min(a, Mathf.Clamp01(d + 2.5f));   // a 2px ring
                    tex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R + 2, R + 2, R + 2, R + 2));
        }

        public static Sprite RoundRect() { return _rr ?? (_rr = MakeRound(false)); }
        public static Sprite RoundRectLine() { return _rrLine ?? (_rrLine = MakeRound(true)); }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Called every IMGUI layout pass with the dialog that should be showing (null for none).</summary>
        public static void Sync(Plugin p, string want)
        {
            if (Failed) return;
            bool built = _canvasGo != null;
            if (want == "msg" && _msgDirty) { _msgDirty = false; Destroy(); built = false; }
            if (want == _kind && (want == null || built)) return;
            Destroy();
            if (want == null) return;
            try
            {
                Build(p, want);
                _kind = want;
            }
            catch (Exception e)
            {
                Failed = true;
                Destroy();
                MainMenuUi.Fields.Clear();
                p.Log.Error("game-styled dialog failed, using the plain windows instead: " + e);
            }
        }

        /// <summary>True while a game-styled dialog is on screen (its text boxes need DrawFields).</summary>
        public static bool Showing { get { return !Failed && _canvasGo != null; } }

        public static void Destroy()
        {
            foreach (var t in _tex) if (t != null) UnityEngine.Object.Destroy(t);
            _tex.Clear();
            if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
            _canvasGo = null; _kind = null;
            MainMenuUi.Fields.Clear();
        }

        static void Build(Plugin p, string kind)
        {
            ResolveTheme();
            MainMenuUi.Fields.Clear();

            _canvasGo = new GameObject("MP_DialogCanvas", typeof(RectTransform));
            _canvasGo.layer = 5;   // UI
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4000;
            var sc = _canvasGo.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            sc.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            var dim = new GameObject("Dim", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            dim.transform.SetParent(_canvasGo.transform, false);
            Stretch((RectTransform)dim.transform);
            dim.GetComponent<Image>().color = new Color(0.0f, 0.02f, 0.06f, 0.62f);   // also swallows clicks behind the dialog
            var cg = dim.GetComponent<CanvasGroup>(); cg.alpha = 0f;

            RectTransform box;
            _onEsc = null;
            if (kind == "join") box = BuildJoin(p, dim.transform);
            else if (kind == "msg") box = BuildMsg(p, dim.transform);
            else box = BuildProfile(p, dim.transform);

            LayoutRebuilder.ForceRebuildLayoutImmediate(box);
            float t0 = Time.unscaledTime;
            var tick = dim.AddComponent<UiTicker>();
            var inner = tick.OnTick;
            tick.OnTick = () =>
            {
                float k = Mathf.Clamp01((Time.unscaledTime - t0) / 0.18f);
                cg.alpha = k;
                box.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, k);
                if (inner != null) inner();
                try { if (_onEsc != null && Input.GetKeyDown(KeyCode.Escape)) _onEsc(); } catch { }
            };
            p.Log.Info("game-styled " + kind + " dialog shown (overlay canvas)");
        }

        // ------------------------------------------------------------------ building blocks

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        static RectTransform Panel(Transform parent, float width)
        {
            var box = new GameObject("Box", typeof(RectTransform), typeof(Image));
            box.transform.SetParent(parent, false);
            var rt = (RectTransform)box.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, 100);
            var back = box.GetComponent<Image>();
            back.sprite = RoundRect(); back.type = Image.Type.Sliced;
            back.color = new Color(0.01f, 0.05f, 0.13f, 0.9f);

            // the game's own panel art as the frame, on top of a dark backing so the text is always readable
            var frame = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            frame.transform.SetParent(box.transform, false);
            frame.AddComponent<LayoutElement>().ignoreLayout = true;
            Stretch((RectTransform)frame.transform);
            var fi = frame.GetComponent<Image>(); fi.raycastTarget = false;
            if (_normal != null) { fi.sprite = _normal; fi.type = _normal.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple; fi.color = new Color(1, 1, 1, 0.9f); }
            else { fi.sprite = RoundRectLine(); fi.type = Image.Type.Sliced; fi.color = new Color(0.45f, 0.78f, 1f, 0.8f); }

            var vl = box.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(46, 46, 38, 38); vl.spacing = 16;
            vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            box.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        static TextMeshProUGUI Txt(Transform parent, string s, float size, Color col, TextAlignmentOptions al = TextAlignmentOptions.MidlineLeft, float minH = 0f)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            if (_fontMat != null) t.fontSharedMaterial = _fontMat;
            t.fontSize = size; t.color = col; t.alignment = al;
            t.enableWordWrapping = true; t.richText = false; t.raycastTarget = false;
            t.text = s;
            if (minH > 0f) go.AddComponent<LayoutElement>().minHeight = minH;
            return t;
        }

        static GameObject Row(Transform parent, float h, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var hl = go.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 14; hl.childAlignment = align;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var le = go.AddComponent<LayoutElement>(); le.minHeight = h; le.preferredHeight = h;
            return go;
        }

        static void Divider(Transform parent)
        {
            var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.color = new Color(0.55f, 0.82f, 1f, 0.25f); im.raycastTarget = false;
            var le = go.AddComponent<LayoutElement>(); le.minHeight = 2; le.preferredHeight = 2;
        }

        static void Spacer(Transform parent, float flexible)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = flexible; le.minWidth = 0;
        }

        /// <summary>A button in the game's blue panel art, with the game's hover sprite.</summary>
        static GameObject Btn(Transform parent, string label, float size, float w, float h, UnityAction onClick, bool primary = false)
        {
            var go = new GameObject("MP_Button_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            if (_normal != null) { img.sprite = _normal; img.type = _normal.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple; img.color = Color.white; }
            else { img.sprite = RoundRect(); img.type = Image.Type.Sliced; img.color = new Color(0.05f, 0.35f, 0.75f, 0.92f); }
            var b = go.GetComponent<Button>();
            b.targetGraphic = img;
            if (_normal != null && _hover != null)
            {
                b.transition = Selectable.Transition.SpriteSwap;
                var ss = b.spriteState; ss.highlightedSprite = _hover; ss.selectedSprite = _hover; ss.pressedSprite = _hover; b.spriteState = ss;
            }
            else
            {
                b.transition = Selectable.Transition.ColorTint;
                var cb = b.colors; cb.highlightedColor = new Color(0.8f, 0.95f, 1f); cb.pressedColor = new Color(0.6f, 0.8f, 1f); cb.selectedColor = cb.highlightedColor; b.colors = cb;
            }
            b.onClick.AddListener(onClick);
            var t = Txt(go.transform, label, size, primary ? White : _textColor, TextAlignmentOptions.Center);
            Stretch((RectTransform)t.transform);
            t.enableWordWrapping = false;
            if (primary) t.fontStyle = FontStyles.Bold;
            var le = go.AddComponent<LayoutElement>(); le.minWidth = w; le.preferredWidth = w; le.minHeight = h; le.preferredHeight = h;
            return go;
        }

        static Color StatusColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return Dim;
            if (s.StartsWith("Could not") || s.Contains("does not look right") || s.Contains("refused") || s.StartsWith("Disconnected") || s.StartsWith("Steam is not")) return Bad;
            if (s.StartsWith("Connected")) return Good;
            return Section;
        }

        // ------------------------------------------------------------------ shared helpers for the bigger dialogs

        static void ClearBox(RectTransform box)
        {
            MainMenuUi.Fields.Clear();
            for (int i = box.childCount - 1; i >= 0; i--)
            {
                var c = box.GetChild(i);
                if (c.name == "Frame") continue;
                c.gameObject.SetActive(false);   // Destroy is deferred; hide now so the layout does not count it
                UnityEngine.Object.Destroy(c.gameObject);
            }
        }

        static void NoFocus() { if (MainMenuUi.Fields.Count > 0) MainMenuUi.Fields[MainMenuUi.Fields.Count - 1].Focus = false; }

        /// <summary>A vertical scroll area of a fixed height. Returns the content transform.</summary>
        static Transform Scroll(Transform parent, float height)
        {
            var go = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            go.transform.SetParent(parent, false);
            var bg = go.GetComponent<Image>(); bg.sprite = RoundRect(); bg.type = Image.Type.Sliced; bg.color = new Color(0.0f, 0.03f, 0.1f, 0.5f);
            var le = go.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; le.flexibleWidth = 1;

            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vp.transform.SetParent(go.transform, false);
            var vrt = (RectTransform)vp.transform; Stretch(vrt); vrt.offsetMin = new Vector2(8, 8); vrt.offsetMax = new Vector2(-8, -8);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(vp.transform, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.offsetMin = new Vector2(0, 0); crt.offsetMax = new Vector2(0, 0);
            var vl = content.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 8; vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = go.GetComponent<ScrollRect>();
            sr.viewport = vrt; sr.content = crt; sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 40f;
            return content.transform;
        }

        static GameObject Tick(Transform parent, Action a)
        {
            var go = new GameObject("Tick", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            go.AddComponent<UiTicker>().OnTick = a;
            return go;
        }

        // ------------------------------------------------------------------ Join: saved servers, direct connect, Steam friends

        static string _view = "list", _eName = "", _eAddr = "", _eError = "";
        static int _eIdx = -1;

        static RectTransform BuildJoin(Plugin p, Transform parent)
        {
            UiServers.Load();
            _view = "list"; _eError = "";
            var box = Panel(parent, 1120);
            RenderJoin(p, box);
            return box;
        }

        static void Go(Plugin p, RectTransform box, string view)
        {
            _view = view; _eError = "";
            RenderJoin(p, box);
        }

        static void RenderJoin(Plugin p, RectTransform box)
        {
            ClearBox(box);
            if (_view == "edit") JoinEdit(p, box);
            else if (_view == "direct") JoinDirect(p, box);
            else JoinList(p, box);
            LayoutRebuilder.ForceRebuildLayoutImmediate(box);
        }

        static TextMeshProUGUI Clip(TextMeshProUGUI t)
        {
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        static void JoinList(Plugin p, RectTransform box)
        {
            _onEsc = () => p.CloseJoinPanel();
            Txt(box, "Join a game", 48, White, TextAlignmentOptions.MidlineLeft, 56);
            Divider(box);

            Txt(box, "Saved servers", 26, Section);
            var content = Scroll(box, 240);
            if (UiServers.List.Count == 0)
                Txt(content, "No saved servers yet. Choose Add server to keep a friend's address here.", 24, Dim, TextAlignmentOptions.MidlineLeft, 60);
            for (int i = 0; i < UiServers.List.Count; i++)
            {
                int idx = i; var e = UiServers.List[i];
                var row = new GameObject("Server", typeof(RectTransform), typeof(Image));
                row.transform.SetParent(content, false);
                var ri = row.GetComponent<Image>(); ri.sprite = RoundRect(); ri.type = Image.Type.Sliced; ri.color = new Color(0.1f, 0.3f, 0.55f, 0.35f); ri.raycastTarget = false;
                var hl = row.AddComponent<HorizontalLayoutGroup>();
                hl.padding = new RectOffset(18, 12, 8, 8); hl.spacing = 12; hl.childAlignment = TextAnchor.MiddleLeft;
                hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
                var rle = row.AddComponent<LayoutElement>(); rle.minHeight = 76; rle.preferredHeight = 76;

                var col = new GameObject("Col", typeof(RectTransform));
                col.transform.SetParent(row.transform, false);
                var cv = col.AddComponent<VerticalLayoutGroup>(); cv.childControlWidth = true; cv.childControlHeight = true; cv.childForceExpandWidth = true; cv.childForceExpandHeight = false;
                var cle = col.AddComponent<LayoutElement>(); cle.flexibleWidth = 1; cle.minWidth = 0;
                Clip(Txt(col.transform, e.Name, 28, White));
                Clip(Txt(col.transform, e.Address, 22, Dim));

                Btn(row.transform, "Join", 26, 130, 54, () => { p.JoinText = e.Address; p.JoinFromText(); }, true);
                Btn(row.transform, "Edit", 26, 120, 54, () => { _eIdx = idx; _eName = e.Name; _eAddr = e.Address; Go(p, box, "edit"); });
                TMP_Text rmLabel = null; bool armed = false;
                var rm = Btn(row.transform, "Remove", 26, 150, 54, () =>
                {
                    if (!armed) { armed = true; if (rmLabel != null) { rmLabel.text = "Sure?"; rmLabel.color = Bad; } return; }
                    UiServers.Remove(idx); RenderJoin(p, box);
                });
                rmLabel = rm.GetComponentInChildren<TMP_Text>();
            }

            var status = Txt(box, "", 25, Dim);
            var br = Row(box, 58);
            Btn(br.transform, "Add server", 27, 220, 58, () => { _eIdx = -1; _eName = ""; _eAddr = ""; Go(p, box, "edit"); });
            Btn(br.transform, "Direct connect", 27, 250, 58, () => Go(p, box, "direct"));
            Spacer(br.transform, 1);
            Btn(br.transform, "Close", 27, 170, 58, () => p.CloseJoinPanel());

            Divider(box);
            Txt(box, "Steam friends", 26, Section);
            var holder = new GameObject("Friends", typeof(RectTransform));
            holder.transform.SetParent(box, false);
            var fv = holder.AddComponent<VerticalLayoutGroup>();
            fv.spacing = 8; fv.childControlWidth = true; fv.childControlHeight = true; fv.childForceExpandWidth = true; fv.childForceExpandHeight = false;

            string key = null; float next = 0f;
            Tick(box, () =>
            {
                var s = p.JoinStatus ?? "";
                status.text = s; status.color = StatusColor(s);
                status.gameObject.SetActive(s.Length > 0);
                if (Time.unscaledTime < next) return;
                next = Time.unscaledTime + 4f;
                List<SteamSupport.FriendInfo> hosting = null;
                string k;
                if (!SteamSupport.Available) k = "nosteam";
                else
                {
                    hosting = SteamSupport.Friends().Where(f => f.Hosting).ToList();
                    k = string.Join("|", hosting.Select(f => f.Id + f.Name).ToArray());
                }
                if (k == key) return;
                key = k;
                for (int i = holder.transform.childCount - 1; i >= 0; i--) { var c = holder.transform.GetChild(i).gameObject; c.SetActive(false); UnityEngine.Object.Destroy(c); }
                if (hosting == null) { Txt(holder.transform, "Steam is not running, or this is not the Steam version of the game. Use a saved server or Direct connect.", 24, Dim); }
                else if (hosting.Count == 0) { Txt(holder.transform, "No friend is hosting right now. They appear here while a multiplayer world is open.", 24, Dim); }
                else
                {
                    foreach (var f in hosting.Take(3))
                    {
                        var fr = f;
                        var r = Row(holder.transform, 54);
                        Txt(r.transform, fr.Name, 28, White).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
                        Btn(r.transform, "Join", 26, 150, 52, () => p.JoinSteam(fr.Id), true);
                    }
                    if (hosting.Count > 3) Txt(holder.transform, "+" + (hosting.Count - 3) + " more hosting", 22, Dim);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(box);
            });
        }

        static void JoinDirect(Plugin p, RectTransform box)
        {
            _onEsc = () => Go(p, box, "list");
            Txt(box, "Direct connect", 48, White, TextAlignmentOptions.MidlineLeft, 56);
            Txt(box, "Type an address: ip, ip:port or steam:id.", 25, Dim);
            Divider(box);
            var row = Row(box, 56);
            MainMenuUi.FieldBox(row.transform, "mp_addr", () => p.JoinText ?? "", v => p.JoinText = v, "203.0.113.9:7777   or   steam:7656...", 80, () => p.JoinFromText());
            Btn(row.transform, "Paste", 26, 150, 56, () => { p.JoinText = (GUIUtility.systemCopyBuffer ?? "").Trim(); });
            var status = Txt(box, "", 25, Dim);
            var br = Row(box, 58);
            Btn(br.transform, "Back", 27, 170, 58, () => Go(p, box, "list"));
            Btn(br.transform, "Save to list...", 27, 250, 58, () => { _eIdx = -1; _eName = ""; _eAddr = (p.JoinText ?? "").Trim(); Go(p, box, "edit"); });
            Spacer(br.transform, 1);
            Btn(br.transform, "Join", 28, 190, 58, () => p.JoinFromText(), true);
            Tick(box, () =>
            {
                var s = p.JoinStatus ?? "";
                status.text = s; status.color = StatusColor(s);
                status.gameObject.SetActive(s.Length > 0);
            });
        }

        static void JoinEdit(Plugin p, RectTransform box)
        {
            _onEsc = () => Go(p, box, "list");
            Txt(box, _eIdx >= 0 ? "Edit server" : "Add server", 48, White, TextAlignmentOptions.MidlineLeft, 56);
            Divider(box);
            Txt(box, "Name", 26, Section);
            var nr = Row(box, 56);
            MainMenuUi.FieldBox(nr.transform, "mp_ename", () => _eName ?? "", v => _eName = v, "Our base", 40, null);
            Txt(box, "Address", 26, Section);
            var ar = Row(box, 56);
            Action save = null;
            MainMenuUi.FieldBox(ar.transform, "mp_eaddr", () => _eAddr ?? "", v => _eAddr = v, "203.0.113.9:7777   or   steam:7656...", 80, () => save());
            NoFocus();
            Btn(ar.transform, "Paste", 26, 150, 56, () => { _eAddr = (GUIUtility.systemCopyBuffer ?? "").Trim(); });
            var err = Txt(box, _eError, 25, Bad); err.gameObject.SetActive(!string.IsNullOrEmpty(_eError));

            Func<bool> commit = () =>
            {
                var t = JoinTarget.Parse(_eAddr);
                if (t.Kind == JoinKind.Invalid)
                {
                    _eError = "That address does not look right (" + t.Error + ").";
                    err.text = _eError; err.gameObject.SetActive(true);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(box);
                    return false;
                }
                UiServers.Put(_eIdx, _eName, _eAddr);
                return true;
            };
            save = () => { if (commit()) Go(p, box, "list"); };

            var br = Row(box, 58);
            Btn(br.transform, "Cancel", 27, 170, 58, () => Go(p, box, "list"));
            Spacer(br.transform, 1);
            Btn(br.transform, "Save", 27, 170, 58, () => save());
            Btn(br.transform, "Save and join", 27, 260, 58, () => { if (commit()) { p.JoinText = _eAddr; Go(p, box, "list"); p.JoinFromText(); } }, true);
        }

        // ------------------------------------------------------------------ name and colour, with a real colour picker

        static float _h, _s = 1f, _v = 1f;
        static string _hex = "";
        static readonly string[] Presets = { "F28C28", "E5484D", "F472B6", "A78BFA", "3FA9F5", "22D3EE", "4ADE80", "FACC15", "F5F5F5", "9CA3AF" };

        static bool ParseHex(string s, out Color c)
        {
            c = Color.white;
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length != 6) return false;
            return ColorUtility.TryParseHtmlString("#" + s, out c);
        }

        static void SetFromColor(Color c)
        {
            float h, s, v;
            Color.RGBToHSV(c, out h, out s, out v);
            if (s > 0.001f && v > 0.001f) _h = h;
            _s = s; _v = v;
        }

        static void PushColor(Plugin p)
        {
            var c = Color.HSVToRGB(_h, _s, _v);
            p.ProfileColor = ColorUtility.ToHtmlStringRGB(c);
            _hex = p.ProfileColor;
        }

        static void LocalNorm(RectTransform rt, PointerEventData e, out float nx, out float ny)
        {
            Vector2 lp;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out lp);
            var r = rt.rect;
            nx = Mathf.Clamp01((lp.x - r.xMin) / Mathf.Max(1f, r.width));
            ny = Mathf.Clamp01((lp.y - r.yMin) / Mathf.Max(1f, r.height));
        }

        static RectTransform BuildProfile(Plugin p, Transform parent)
        {
            _onEsc = null;
            Color start; if (!ParseHex(p.ProfileColor, out start)) { start = Color.white; p.ProfileColor = "FFFFFF"; }
            SetFromColor(start); _hex = (p.ProfileColor ?? "").TrimStart('#');

            var box = Panel(parent, 1000);
            Txt(box, "Choose your player", 48, White, TextAlignmentOptions.MidlineLeft, 56);
            Txt(box, "Everyone in the world sees this name and colour above your player.", 25, Dim);
            Divider(box);

            Txt(box, "Name", 26, Section);
            var nr = Row(box, 56);
            MainMenuUi.FieldBox(nr.transform, "mp_name", () => p.ProfileName ?? "", v => p.ProfileName = v, "Your name", 24, () => p.CloseProfilePanel(true));

            Txt(box, "Colour", 26, Section);
            var pr = Row(box, 280, TextAnchor.UpperLeft);
            ((HorizontalLayoutGroup)pr.GetComponent<HorizontalLayoutGroup>()).spacing = 24;

            // saturation / value square
            var svTex = new Texture2D(48, 48, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var hueTex = new Texture2D(1, 96, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _tex.Add(svTex); _tex.Add(hueTex);
            for (int y = 0; y < 96; y++) hueTex.SetPixel(0, y, Color.HSVToRGB(y / 95f, 1f, 1f));
            hueTex.Apply();

            var sv = new GameObject("SV", typeof(RectTransform), typeof(RawImage));
            sv.transform.SetParent(pr.transform, false);
            sv.GetComponent<RawImage>().texture = svTex;
            var sle = sv.AddComponent<LayoutElement>(); sle.minWidth = 280; sle.preferredWidth = 280; sle.minHeight = 280; sle.preferredHeight = 280;
            var svRt = (RectTransform)sv.transform;
            var svDrag = sv.AddComponent<UiDrag>();

            var hue = new GameObject("Hue", typeof(RectTransform), typeof(RawImage));
            hue.transform.SetParent(pr.transform, false);
            hue.GetComponent<RawImage>().texture = hueTex;
            var hle = hue.AddComponent<LayoutElement>(); hle.minWidth = 44; hle.preferredWidth = 44; hle.minHeight = 280; hle.preferredHeight = 280;
            var hueRt = (RectTransform)hue.transform;
            var hueDrag = hue.AddComponent<UiDrag>();

            // markers
            var dotBack = new GameObject("DotBack", typeof(RectTransform), typeof(Image)); dotBack.transform.SetParent(sv.transform, false);
            var dotRt = (RectTransform)dotBack.transform; dotRt.sizeDelta = new Vector2(26, 26);
            var db = dotBack.GetComponent<Image>(); db.sprite = MainMenuUi.CircleSprite(); db.color = Color.black; db.raycastTarget = false;
            var dotIn = new GameObject("Dot", typeof(RectTransform), typeof(Image)); dotIn.transform.SetParent(dotBack.transform, false);
            var dInRt = (RectTransform)dotIn.transform; dInRt.sizeDelta = new Vector2(18, 18);
            var di = dotIn.GetComponent<Image>(); di.sprite = MainMenuUi.CircleSprite(); di.color = Color.white; di.raycastTarget = false;
            var bar = new GameObject("Bar", typeof(RectTransform), typeof(Image)); bar.transform.SetParent(hue.transform, false);
            var barRt = (RectTransform)bar.transform; barRt.sizeDelta = new Vector2(8, 8);
            var bi = bar.GetComponent<Image>(); bi.color = Color.white; bi.raycastTarget = false;
            var bo = bar.AddComponent<Outline>(); bo.effectColor = Color.black; bo.effectDistance = new Vector2(2, -2);

            // right column: preview, hex, presets
            var right = new GameObject("Right", typeof(RectTransform));
            right.transform.SetParent(pr.transform, false);
            var rv = right.AddComponent<VerticalLayoutGroup>();
            rv.spacing = 12; rv.childControlWidth = true; rv.childControlHeight = true; rv.childForceExpandWidth = true; rv.childForceExpandHeight = false;
            right.AddComponent<LayoutElement>().flexibleWidth = 1;
            var preview = Txt(right.transform, "", 36, Color.white, TextAlignmentOptions.MidlineLeft, 52);
            Clip(preview);
            Txt(right.transform, "Hex code", 22, Dim);
            var hr = Row(right.transform, 56);
            MainMenuUi.FieldBox(hr.transform, "mp_hex", () => _hex ?? "", v =>
            {
                _hex = v;
                Color c;
                if (ParseHex(v, out c)) { SetFromColor(c); p.ProfileColor = ColorUtility.ToHtmlStringRGB(c); }
            }, "RRGGBB", 7, null);
            NoFocus();
            Txt(right.transform, "Presets", 22, Dim);
            var grid = new GameObject("Presets", typeof(RectTransform));
            grid.transform.SetParent(right.transform, false);
            var gl = grid.AddComponent<GridLayoutGroup>();
            gl.cellSize = new Vector2(46, 46); gl.spacing = new Vector2(10, 10); gl.constraint = GridLayoutGroup.Constraint.FixedColumnCount; gl.constraintCount = 5;
            foreach (var hx in Presets)
            {
                var h = hx;
                var sw = new GameObject("Preset_" + h, typeof(RectTransform), typeof(Image), typeof(Button));
                sw.transform.SetParent(grid.transform, false);
                Color col; ColorUtility.TryParseHtmlString("#" + h, out col);
                var im = sw.GetComponent<Image>(); im.sprite = MainMenuUi.CircleSprite(); im.color = col;
                var bt = sw.GetComponent<Button>(); bt.targetGraphic = im;
                var cb = bt.colors; cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f); bt.colors = cb;
                bt.onClick.AddListener(() => { SetFromColor(col); p.ProfileColor = h; _hex = h; });
            }

            Divider(box);
            var br = Row(box, 58);
            Spacer(br.transform, 1);
            Btn(br.transform, "Done", 28, 220, 58, () => p.CloseProfilePanel(true), true);

            // pointer interaction
            svDrag.Move = e => { float nx, ny; LocalNorm(svRt, e, out nx, out ny); _s = nx; _v = ny; PushColor(p); };
            hueDrag.Move = e => { float nx, ny; LocalNorm(hueRt, e, out nx, out ny); _h = Mathf.Min(ny, 0.999f); PushColor(p); };

            float lastH = -1f;
            var px = new Color32[48 * 48];
            Tick(box, () =>
            {
                if (Mathf.Abs(_h - lastH) > 0.0005f)
                {
                    lastH = _h;
                    for (int y = 0; y < 48; y++)
                        for (int x = 0; x < 48; x++)
                            px[y * 48 + x] = Color.HSVToRGB(_h, x / 47f, y / 47f);
                    svTex.SetPixels32(px); svTex.Apply();
                }
                dotRt.anchorMin = dotRt.anchorMax = new Vector2(_s, _v); dotRt.anchoredPosition = Vector2.zero;
                barRt.anchorMin = new Vector2(0, _h); barRt.anchorMax = new Vector2(1, _h); barRt.anchoredPosition = Vector2.zero;
                barRt.sizeDelta = new Vector2(10, 8);
                Color c; if (!ParseHex(p.ProfileColor, out c)) c = Color.white;
                dotIn.GetComponent<Image>().color = c;
                var n = (p.ProfileName ?? "").Trim();
                preview.text = n.Length > 0 ? n : "(your name)";
                preview.color = n.Length > 0 ? c : new Color(c.r, c.g, c.b, 0.5f);
            });
            return box;
        }

        // ------------------------------------------------------------------ a general message dialog (title, text, one or two buttons)

        sealed class MsgInfo { public string Title, Body, Ok, Cancel; public Action OnOk, OnCancel; }
        static MsgInfo _msg;
        static bool _msgDirty;

        /// <summary>True while a message dialog is requested. Gui.Draw reads this.</summary>
        public static bool MessageShowing { get { return _msg != null; } }

        /// <summary>
        /// Shows a game-styled message with an OK button and an optional second button (pass null to leave it out). The
        /// action runs, then the dialog closes. Returns false when the game-styled dialogs are unavailable: the caller
        /// should then draw its own IMGUI window. Call CloseMessage() to dismiss it from code.
        /// </summary>
        public static bool ShowMessage(string title, string body, string okLabel, Action onOk, string cancelLabel = null, Action onCancel = null)
        {
            if (Failed) return false;
            _msg = new MsgInfo { Title = title, Body = body ?? "", Ok = okLabel ?? "OK", OnOk = onOk, Cancel = cancelLabel, OnCancel = onCancel };
            _msgDirty = true;
            return true;
        }

        public static void CloseMessage() { _msg = null; }

        static RectTransform BuildMsg(Plugin p, Transform parent)
        {
            var m = _msg;
            var box = Panel(parent, 1000);
            Txt(box, m.Title ?? "", 44, White, TextAlignmentOptions.MidlineLeft, 54);
            Divider(box);
            if (m.Body.Length > 420 || m.Body.Split('\n').Length > 8)
            {
                var c = Scroll(box, 360);
                Txt(c, m.Body, 25, White);
            }
            else Txt(box, m.Body, 26, White);
            var br = Row(box, 58);
            Spacer(br.transform, 1);
            if (m.Cancel != null) Btn(br.transform, m.Cancel, 27, 220, 58, () => { _msg = null; if (m.OnCancel != null) m.OnCancel(); });
            Btn(br.transform, m.Ok, 27, 260, 58, () => { _msg = null; if (m.OnOk != null) m.OnOk(); }, true);
            _onEsc = m.Cancel != null ? (Action)(() => { _msg = null; if (m.OnCancel != null) m.OnCancel(); }) : null;
            return box;
        }
    }

    /// <summary>Pointer press and drag on a uGUI element (colour picker).</summary>
    sealed class UiDrag : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IDragHandler
    {
        public Action<UnityEngine.EventSystems.PointerEventData> Move;
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { if (Move != null) Move(e); }
        public void OnDrag(UnityEngine.EventSystems.PointerEventData e) { if (Move != null) Move(e); }
    }
}
