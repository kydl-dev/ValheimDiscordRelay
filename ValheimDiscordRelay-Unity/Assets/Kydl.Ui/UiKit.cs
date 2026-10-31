using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Kydl.Ui
{
    /// <summary>
    /// SHARED UI KIT of all the user's mods (E:\Projects\Valheim\Claude\shared\Kydl.Ui). Every mod compiles these files into its own DLL
    /// (internal types, so mods never clash), and the Unity project of every mod gets a synced copy for the preview (KydlUiSync).
    /// Change it HERE only; every mod picks the change up on its next build.
    /// One runtime UI Toolkit panel holds every window of the mod (at most 70% of the screen high and 60% wide, movable, the character does
    /// not move while it is open - UiInputLock in Kydl.Common).
    /// Look (user, DrainTarPit T-58: "similar to" the FollowPlayer mod's menu, bigger text): the FollowPlayer look - flat dark
    /// panels, a gold accent line on top, Norse everywhere (Norsebold for titles; the KydlCore build, KYDL_CORE_PUBLIC, uses NotoSansSC-Regular, D-043), upper-case gold section titles with a line, pill switches,
    /// thin sliders with a typed number box, a slim gold scrollbar. Every font falls back to the game's NotoEmoji font (emoji in any font).
    /// All looks are inline styles: a game build has no USS importer / default theme. The panel scales with the screen height (1080p ref).
    /// </summary>
#if KYDL_CORE_PUBLIC
    public static class UiKit
#else
    internal static class UiKit
#endif
    {
        // palette (from FollowPlayer's Theme; dim text a little brighter: the user found grey notes hard to read)
        /// <summary>Window / panel background (Panel) and the lighter row and button background (PanelLight).</summary>
        public static readonly Color Panel = new(0.075f, 0.058f, 0.043f, 0.96f), PanelLight = new(0.125f, 0.098f, 0.072f, 0.96f);
        /// <summary>Hovered row or button background (PanelHover) and the brown line colour of frames and separators (Border).</summary>
        public static readonly Color PanelHover = new(0.190f, 0.145f, 0.098f, 0.96f), Border = new(0.45f, 0.33f, 0.17f, 0.95f);
        /// <summary>The gold accent (values, active parts) and its darker form (primary button, thumbs). KydlCore build: #DAA520 (D-030 / D-032), the others keep the old orange-gold.</summary>
#if KYDL_CORE_PUBLIC
        public static readonly Color Gold = new(218 / 255f, 165 / 255f, 32 / 255f, 1f), GoldDim = new(157 / 255f, 119 / 255f, 23 / 255f, 1f);
#else
        public static readonly Color Gold = new(1.00f, 0.72f, 0.36f, 1f), GoldDim = new(0.72f, 0.52f, 0.26f, 1f);
#endif
        /// <summary>Normal text and the dimmer text of notes and hints.</summary>
        public static readonly Color Text = new(0.92f, 0.87f, 0.77f, 1f), TextDim = new(0.70f, 0.64f, 0.55f, 1f);
        /// <summary>The very dark background of input fields and slider tracks (Track) and dark text on gold (OnDark).</summary>
        public static readonly Color Track = new(0.03f, 0.025f, 0.02f, 1f), OnDark = new(0.10f, 0.07f, 0.03f, 1f);
        /// <summary>Colour of the NAMES (tabs, mods, settings, section titles) and of the VALUES (numbers, switches, fields) - D-032: a clear split between names and values with few colours. KydlCore build: setting names #FCF5ED (D-048), values = Gold (#DAA520); the others keep Text / Gold as before.</summary>
#if KYDL_CORE_PUBLIC
        public static readonly Color NameColor = new(252 / 255f, 245 / 255f, 237 / 255f, 1f), ValueColor = Gold;
#else
        public static readonly Color NameColor = Text, ValueColor = Gold;
#endif
        /// <summary>Colour of mod names (the window title, later the hub's mod tabs) and of settings-tab names; also the thin border of a hovered button. KydlCore build: red #DB2121 (D-048); the others keep Gold.</summary>
#if KYDL_CORE_PUBLIC
        public static readonly Color TabColor = new(219 / 255f, 33 / 255f, 33 / 255f, 1f);
#else
        public static readonly Color TabColor = Gold;
#endif

        // sizes (FollowPlayer: 12 - 26; user: "a bit bigger")
        internal const int TitleSize = 34;
        /// <summary>Text sizes in px at the 1080p reference: section titles, normal text, button text, small text and notes.</summary>
        public const int SectionSize = 24, FontSize = 22, ButtonSize = 21, SmallSize = 18;
        /// <summary>Text size of the NAMES (setting and switch names). KydlCore build: a little bigger than normal text instead of bold (D-043); the others = FontSize.</summary>
#if KYDL_CORE_PUBLIC
        public const int NameSize = FontSize + 2;
#else
        public const int NameSize = FontSize;
#endif

        // window height and top edge in % of the screen (the window is centred up and down); KydlCore build: 80% high (D-048), the others 70%
#if KYDL_CORE_PUBLIC
        const float WindowHeight = 80f, WindowTop = 10f;
#else
        const float WindowHeight = 70f, WindowTop = 15f;
#endif

        /// Who owns this panel (the mod's name: GameObject name and log lines). Set it before the first window (Plugin.Awake); default = the DLL name.
        public static string Owner = typeof(UiKit).Assembly.GetName().Name;

        static UIDocument _doc;
        static Font _bodyFont, _titleFont;
        static FontAsset _bodyAsset, _titleAsset;

        /// The panel's settings (the Unity preview renders the panel into a texture through them).
        public static PanelSettings Settings { get; private set; }

        /// The full-screen layer every window is added to (picking ignored). null when UI Toolkit failed.
        public static VisualElement Root
        {
            get
            {
                if (_doc == null) Create();
                return _doc != null ? _doc.rootVisualElement : null;
            }
        }

        /// Throws the panel away (the Unity preview rebuilds it in every Play).
        internal static void Reset()
        {
            if (_doc != null) Object.DestroyImmediate(_doc.gameObject);
            _doc = null;
        }

        static void Create()
        {
            try
            {
                var settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.name = "Kydl_PanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;          // bigger on big screens (user: "hard to read" at 3440x1440)
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.match = 1f;                                              // by height: ultrawide screens get the same text size as 16:9
                settings.sortingOrder = 500;
                Settings = settings;
                var go = new GameObject("Kydl_UI_" + Owner);
                if (Application.isPlaying) Object.DontDestroyOnLoad(go);
                _doc = go.AddComponent<UIDocument>();
                _doc.panelSettings = settings;
                var root = _doc.rootVisualElement;
                root.style.position = Position.Absolute;
                root.style.left = 0; root.style.top = 0; root.style.right = 0; root.style.bottom = 0;
                root.pickingMode = PickingMode.Ignore;
                string fonts = LoadFonts();
                Font(root, false);
                root.style.color = Text;
                root.style.fontSize = FontSize;
                UiLog.Verbose("UiKit: UI Toolkit panel made (scale with screen " + Screen.width + "x" + Screen.height + " from 1920x1080, " + fonts + ")");
            }
            catch (System.Exception e)
            {
                UiLog.Error("UiKit: UI Toolkit could not be started - the menus of this mod are not available: " + e);
                _doc = null;
            }
        }

        // --- fonts --------------------------------------------------------------------------------------------------------------
#if KYDL_CORE_PUBLIC
        // KydlCore look (D-043): NotoSansSC-Regular, the font Valheim itself ships, for text AND titles (it has no Bold weight: titles are only bigger).
        // Not loaded (a game update renamed it): any loaded font and ONE warning (D-044). Mods that are not moved to KydlCore keep the old Norse look.
        const string TextFontName = "NotoSansSC-Regular";

        static string LoadFonts()
        {
            Font emoji = null, norse = null, any = null;
            _bodyFont = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null) continue;
                string n = f.name;
                if (n == TextFontName) _bodyFont = f;
                else if (n == "NotoEmoji-Regular") emoji = f;
                else if (n == "Norse") norse = f;
                else if (any == null) any = f;
            }
            if (_bodyFont == null)
            {
                _bodyFont = norse != null ? norse : any;
                UiLog.Warn("UiKit: font '" + TextFontName + "' is not loaded; using " + (_bodyFont != null ? "'" + _bodyFont.name + "'" : "no font (engine default)"));
            }
            _titleFont = _bodyFont;
            return MakeAssets(emoji);
        }
#else
        static string LoadFonts()
        {
            Font emoji = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null) continue;
                string n = f.name;
                if (n == "Norse") _bodyFont = f;
                else if (n == "Norsebold") _titleFont = f;
                else if (n == "NotoEmoji-Regular") emoji = f;
            }
            if (_titleFont == null) _titleFont = _bodyFont;
            if (_bodyFont == null) _bodyFont = _titleFont;
            return MakeAssets(emoji);
        }
#endif

        /// Font assets for the chosen text / title fonts, each with the emoji font as fallback; returns a log description.
        static string MakeAssets(Font emoji)
        {
            if (emoji == null) emoji = UnityEngine.Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI Emoji", "Segoe UI Symbol", "Noto Emoji" }, 32);
            var emojiAsset = MakeAsset(emoji, null);
            _bodyAsset = MakeAsset(_bodyFont, emojiAsset);
            _titleAsset = _titleFont == _bodyFont ? _bodyAsset : MakeAsset(_titleFont, emojiAsset);
            return "text font " + Describe(_bodyFont, _bodyAsset) + ", title font " + Describe(_titleFont, _titleAsset) + ", emoji font " + Describe(emoji, emojiAsset);
        }

        static string Describe(Font f, FontAsset a) { return f == null ? "MISSING" : "'" + f.name + "'" + (a != null ? " (font asset made)" : " (legacy font, no emoji fallback)"); }

        /// A TextCore font asset made from a game font, with `fallback` for the glyphs it does not have (emoji). null = the legacy font is used.
        static FontAsset MakeAsset(Font f, FontAsset fallback)
        {
            if (f == null) return null;
            try
            {
                var a = FontAsset.CreateFontAsset(f);
                if (a == null) { UiLog.Warn("UiKit: font asset from '" + f.name + "' could not be made"); return null; }
                a.name = "Kydl_" + f.name;
                if (fallback != null)
                {
                    if (a.fallbackFontAssetTable == null) a.fallbackFontAssetTable = new List<FontAsset>();
                    a.fallbackFontAssetTable.Add(fallback);
                }
                return a;
            }
            catch (System.Exception e) { UiLog.Warn("UiKit: font asset from '" + f.name + "' failed: " + e.Message); return null; }
        }

        /// Puts the text font (or the title font) on an element.
        internal static void Font(VisualElement e, bool title)
        {
            var asset = title ? _titleAsset : _bodyAsset;
            var font = title ? _titleFont : _bodyFont;
            if (asset != null) e.style.unityFontDefinition = FontDefinition.FromSDFFont(asset);
            else if (font != null) e.style.unityFont = font;
        }

        // --- style helpers ------------------------------------------------------------------------------------------------------
        /// <summary>Rounds all four corners of an element with radius r (px).</summary>
        public static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r; e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        /// <summary>Gives an element a border of width w (px) and colour c on all four sides.</summary>
        public static void Borders(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = w; e.style.borderRightWidth = w; e.style.borderBottomWidth = w; e.style.borderLeftWidth = w;
            e.style.borderTopColor = c; e.style.borderRightColor = c; e.style.borderBottomColor = c; e.style.borderLeftColor = c;
        }

        /// <summary>Pads an element: v px above and below, h px left and right.</summary>
        public static void Pad(VisualElement e, float v, float h)
        {
            e.style.paddingTop = v; e.style.paddingBottom = v; e.style.paddingLeft = h; e.style.paddingRight = h;
        }

        /// <summary>A short colour / position fade (0.12 s) on the named style properties, like FollowPlayer's buttons and switches.</summary>
        public static void Fade(VisualElement e, params string[] props)
        {
            var names = new List<StylePropertyName>();
            foreach (var p in props) names.Add(new StylePropertyName(p));
            e.style.transitionProperty = names;
            e.style.transitionDuration = new List<TimeValue> { new TimeValue(0.12f, TimeUnit.Second) };
        }

        /// <summary>A text label (not clickable) with a size and colour; title = the title font.</summary>
        public static Label Label(string text, float size, Color color, bool title = false)
        {
            var l = new Label(text);
            l.pickingMode = PickingMode.Ignore;
            l.style.fontSize = size; l.style.color = color;
            if (title) Font(l, true);
            return l;
        }

        // --- window ---------------------------------------------------------------------------------------------------------------
        /// A movable window (drag the title bar): fixed 70% of the screen high (the KydlCore build: 80%, D-048; taller content scrolls), 760 wide (at most 60%), its right side
        /// 5% from the right screen edge (user: so the sign stays visible while it is edited).
        public static VisualElement Window(string title, string footer, System.Action onClose, out UiScroll body)
        {
            var w = new VisualElement { name = "kydl-window" };
            w.style.position = Position.Absolute;
            w.style.width = 760; w.style.maxWidth = Length.Percent(60);
            w.style.height = Length.Percent(WindowHeight);
            w.style.right = Length.Percent(5); w.style.left = StyleKeyword.Auto; w.style.top = Length.Percent(WindowTop);
            UiLog.Verbose("UiKit: window '" + title + "' opened 5% from the right edge, " + WindowTop.ToString("0") + "% from the top, " + WindowHeight.ToString("0") + "% of the screen high");
            w.style.backgroundColor = Panel;
            Radius(w, 8);
            Borders(w, 2, Border);
            w.style.borderTopColor = Gold;                                      // accent
            w.style.flexDirection = FlexDirection.Column;
            w.pickingMode = PickingMode.Position;
            w.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());    // clicks never reach the game

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row; head.style.justifyContent = Justify.SpaceBetween; head.style.alignItems = Align.Center;
            head.style.flexShrink = 0;
            Pad(head, 12, 18);
            head.style.borderBottomWidth = 1; head.style.borderBottomColor = Border;
#if KYDL_CORE_PUBLIC
            var t = Label(title, TitleSize, TabColor, true);                    // the mod name as written (PascalCase), red (D-048)
#else
            var t = Label(title.ToUpperInvariant(), TitleSize, Gold, true);
#endif
            t.style.letterSpacing = 2f;
            head.Add(t);
            head.Add(UiControls.Button("X", onClose));
            w.Add(head);
            Drag(head, w);

            body = new UiScroll(title);
            Pad(body.Content, 14, 18);
            w.Add(body.Root);

            var foot = Label(footer ?? "", SmallSize, TextDim);
            foot.style.alignSelf = Align.Center; foot.style.marginTop = 4; foot.style.marginBottom = 8; foot.style.flexShrink = 0;
            w.Add(foot);
            return w;
        }

        /// Dragging `handle` moves `target` (kept on screen).
        static void Drag(VisualElement handle, VisualElement target)
        {
            bool dragging = false;
            Vector2 start = Vector2.zero, origin = Vector2.zero;
            handle.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || e.target is Button) return;
                dragging = true; start = e.position; origin = new Vector2(target.layout.x, target.layout.y);   // placed by right %: take the laid-out position
                target.style.right = StyleKeyword.Auto;                                                        // from now on placed by left / top
                handle.CapturePointer(e.pointerId);
            });
            handle.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                var p = origin + (Vector2)e.position - start;
                var parent = target.parent;
                float maxX = parent != null ? parent.resolvedStyle.width - 80f : 2000f, maxY = parent != null ? parent.resolvedStyle.height - 40f : 2000f;
                target.style.left = Mathf.Clamp(p.x, -target.resolvedStyle.width + 80f, maxX);
                target.style.top = Mathf.Clamp(p.y, 0f, maxY);
            });
            handle.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                handle.ReleasePointer(e.pointerId);
                UiLog.Verbose("UiKit: window moved to " + target.resolvedStyle.left.ToString("0") + ", " + target.resolvedStyle.top.ToString("0"));
            });
        }
    }
}
