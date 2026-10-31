using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kydl.Ui
{
    /// <summary>
    /// SHARED (added 2026-10-26 for ChestDecor; user: "shared + brightness adjustments"). The FollowPlayer colour editor of UiControls.ColorPicker
    /// (preview bar, preset swatches, Red / Green / Blue sliders, hex box) as an object, with an optional BRIGHTNESS slider (0 - 100 %: the HSV
    /// value; hue and saturation stay, also through black). Sliders are UiSlider objects that are moved, never rebuilt, so a drag is never cut off
    /// (the T-65 problem of the older picker). `changed` gets "#RRGGBB". UiControls.ColorPicker is unchanged for the older menus.
    /// </summary>
#if KYDL_CORE_PUBLIC
    public sealed class UiColorPicker
#else
    internal sealed class UiColorPicker
#endif
    {
        static readonly string[] Palette = { "#FFFFFF", "#000000", "#E8D5A9", "#FFB85C", "#C0392B", "#E67E22", "#F1C40F", "#2ECC71", "#1ABC9C", "#00FFFF", "#3498DB", "#9B59B6", "#FF33FF", "#7F8C8D" };

        /// <summary>The colour editor element to add to a parent.</summary>
        public readonly VisualElement Root;
        readonly VisualElement _preview;
        readonly UiSlider _r, _g, _b, _bright;
        readonly TextField _hex;
        readonly Action<string> _changed;
        Color _c;
        float _h, _s;               // kept separately: at brightness 0 the colour alone has no hue / saturation

        /// <summary>The colour as "#RRGGBB".</summary>
        public string Hex { get { return "#" + ColorUtility.ToHtmlStringRGB(_c); } }
        /// <summary>The colour.</summary>
        public Color Color { get { return _c; } }

        /// <summary>A colour editor starting at `hex` ("#RRGGBB"); `changed` gets "#RRGGBB" for every change made by the user; `brightness` adds the 0 - 100 % brightness slider.</summary>
        public UiColorPicker(string hex, Action<string> changed, bool brightness)
        {
            _changed = changed;
            if (!ColorUtility.TryParseHtmlString(hex ?? "#FFFFFF", out _c)) _c = Color.white;
            _c.a = 1f;
            float v; Color.RGBToHSV(_c, out _h, out _s, out v);

            Root = new VisualElement(); Root.style.flexShrink = 0;
            _preview = new VisualElement(); _preview.style.height = 26; UiKit.Radius(_preview, 4); UiKit.Borders(_preview, 1, UiKit.Border);
            Root.Add(_preview);

            var pal = new VisualElement(); pal.style.flexDirection = FlexDirection.Row; pal.style.flexWrap = Wrap.Wrap; pal.style.flexShrink = 0;
            pal.style.marginTop = 8; pal.style.marginBottom = 4;
            foreach (var p in Palette)
            {
                Color pc; ColorUtility.TryParseHtmlString(p, out pc);
                var sw = new VisualElement(); sw.style.width = 28; sw.style.height = 28; sw.style.marginRight = 6; sw.style.marginTop = 4;
                sw.style.backgroundColor = pc; UiKit.Radius(sw, 4); UiKit.Borders(sw, 1, UiKit.Border);
                sw.RegisterCallback<PointerEnterEvent>(e => UiKit.Borders(sw, 2, UiKit.Gold));
                sw.RegisterCallback<PointerLeaveEvent>(e => UiKit.Borders(sw, 1, UiKit.Border));
                sw.RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; UiLog.Verbose("UiColorPicker: swatch " + p); SetColor(pc, true, true); });
                pal.Add(sw);
            }
            Root.Add(pal);

            _r = new UiSlider(0, 255, _c.r * 255f, "0", x => { _c.r = x / 255f; FromRgb(); });
            _g = new UiSlider(0, 255, _c.g * 255f, "0", x => { _c.g = x / 255f; FromRgb(); });
            _b = new UiSlider(0, 255, _c.b * 255f, "0", x => { _c.b = x / 255f; FromRgb(); });
            Root.Add(UiControls.Row("Red", _r.Root));
            Root.Add(UiControls.Row("Green", _g.Root));
            Root.Add(UiControls.Row("Blue", _b.Root));
            if (brightness)
            {
                _bright = new UiSlider(0, 100, v * 100f, "0", OnBrightness, new[] { 0f, 0.25f, 0.5f, 0.75f, 1f });
                Root.Add(UiControls.Row("Brightness", _bright.Root));
            }
            _hex = UiControls.TextField(Hex, 7, OnHexTyped);
            _hex.style.width = 130; _hex.style.flexGrow = 0;
            var hexRow = UiControls.Row("Hex", _hex); _hex.style.flexGrow = 0;
            Root.Add(hexRow);
            Show(true);
        }

        void OnBrightness(float percent)
        {
            float v = Mathf.Clamp01(percent / 100f);
            var c = Color.HSVToRGB(_h, _s, v); c.a = 1f;
            _c = c;
            UiLog.Verbose("UiColorPicker: brightness " + percent.ToString("0") + "% -> " + Hex);
            Show(false, true);
            _changed?.Invoke(Hex);
        }

        void FromRgb()
        {
            float v, h, s; Color.RGBToHSV(_c, out h, out s, out v);
            if (v > 0.001f && s > 0.001f) { _h = h; _s = s; }
            else if (v > 0.001f) _s = s;          // grey: hue undefined, keep the last one
            Show(false);
            _changed?.Invoke(Hex);
        }

        void OnHexTyped(string text)
        {
            Color p;
            if (text == null || text.TrimStart('#').Length != 6 || !ColorUtility.TryParseHtmlString(text.StartsWith("#") ? text : "#" + text, out p)) return;
            if (ColorUtility.ToHtmlStringRGB(p) == ColorUtility.ToHtmlStringRGB(_c)) return;
            UiLog.Verbose("UiColorPicker: hex typed " + text);
            SetColor(p, false, true);
        }

        /// Shows the colour on the sliders, the preview and the hex box. `keepRgb` = the RGB sliders already show it (they are being dragged).
        void Show(bool all, bool fromBrightness = false)
        {
            _preview.style.backgroundColor = _c;
            if (all || fromBrightness) { _r.SetValueWithoutNotify(_c.r * 255f); _g.SetValueWithoutNotify(_c.g * 255f); _b.SetValueWithoutNotify(_c.b * 255f); }
            if (_bright != null && !fromBrightness) { float h, s, v; Color.RGBToHSV(_c, out h, out s, out v); _bright.SetValueWithoutNotify(v * 100f); }
            _hex.SetValueWithoutNotify(Hex);
        }

        void SetColor(Color c, bool updateHex, bool notify)
        {
            c.a = 1f; _c = c;
            float h, s, v; Color.RGBToHSV(_c, out h, out s, out v);
            if (v > 0.001f && s > 0.001f) { _h = h; _s = s; } else if (v > 0.001f) _s = s;
            Show(true);
            if (!updateHex) _hex.SetValueWithoutNotify(_hex.value);
            if (notify) _changed?.Invoke(Hex);
        }

        /// Shows another colour (e.g. a different part was selected) without calling `changed`. Expected outcome: Hex equals the given colour.
        public bool Set(Color c)
        {
            SetColor(c, true, false);
            return ColorUtility.ToHtmlStringRGB(_c) == ColorUtility.ToHtmlStringRGB(new Color(c.r, c.g, c.b, 1f));
        }
    }
}
