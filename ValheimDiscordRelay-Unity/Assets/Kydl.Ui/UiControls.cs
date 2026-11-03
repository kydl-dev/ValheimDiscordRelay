using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace KydlCore.Ui
{
    /// <summary>
    /// T-023 / T-58: the controls of the mod's menus in the FollowPlayer look (UiKit): flat buttons (primary = gold), upper-case section titles,
    /// pill switches, thin sliders with a round handle and a typed number box, drop-downs, text fields, a colour editor. Plain VisualElements
    /// with inline styles (no theme needed). Nothing shrinks (flexShrink 0): the window scrolls instead.
    /// </summary>
    public static class UiControls
    {
        // --- button ---------------------------------------------------------------------------------------------------------
        const float PressDim = 0.35f;          // share of black mixed in while the button is pressed (D-048)
        const long PressFeedbackMs = 120;      // the dimmed look stays this long after the release, so a quick click is still seen

        /// A gold (primary) or dark push button; logs every press. The KydlCore build also dims it while it is pressed and draws a thin red border while the pointer is over it (D-048).
        public static Button Button(string text, Action onClick, bool primary = false)
        {
            var b = new Button(UiLog.Wrap(() => { UiLog.Verbose("Ui: button '" + text + "' pressed"); onClick?.Invoke(); })) { text = text };
            Color bg = primary ? UiKit.GoldDim : UiKit.PanelLight, hot = primary ? UiKit.Gold : UiKit.PanelHover;
            b.style.backgroundColor = bg;
            b.style.color = primary ? UiKit.OnDark : UiKit.Text;
            b.style.fontSize = UiKit.ButtonSize;
            UiKit.Borders(b, 1, UiKit.Border); UiKit.Radius(b, 4); UiKit.Pad(b, 6, 14);
            b.style.flexShrink = 0;
            b.style.marginLeft = 3; b.style.marginRight = 3; b.style.marginTop = 3; b.style.marginBottom = 3;
            UiKit.Fade(b, "background-color");
            bool hovered = false, pressed = false;
            Action show = () =>
            {
                b.style.backgroundColor = pressed ? Color.Lerp(hot, Color.black, PressDim) : hovered ? hot : bg;
                UiKit.Borders(b, 1, hovered ? UiKit.TabColor : UiKit.Border);
            };
            b.RegisterCallback<PointerEnterEvent>(e => { hovered = true; show(); });
            b.RegisterCallback<PointerLeaveEvent>(e => { hovered = false; pressed = false; show(); });
            b.RegisterCallback<PointerDownEvent>(e => { pressed = true; show(); UiLog.Verbose("Ui: button '" + text + "' down, dimmed " + (PressDim * 100f).ToString("0") + "% for at least " + PressFeedbackMs + " ms"); });
            b.RegisterCallback<PointerUpEvent>(e => b.schedule.Execute(() => { pressed = false; show(); }).StartingIn(PressFeedbackMs));
            return b;
        }

        /// A button that looks like an input field (dark, text on the left): the closed drop-down.
        public static Button Field(string text, Action onClick)
        {
            var b = new Button(UiLog.Wrap(() => { UiLog.Verbose("Ui: field '" + text + "' opened"); onClick?.Invoke(); })) { text = text };
            b.style.backgroundColor = UiKit.Track; b.style.color = UiKit.ValueColor; b.style.fontSize = UiKit.FontSize;
            UiKit.Borders(b, 1, UiKit.Border); UiKit.Radius(b, 4); UiKit.Pad(b, 5, 10);
            b.style.unityTextAlign = TextAnchor.MiddleLeft;
            b.style.flexShrink = 0; b.style.marginLeft = 0; b.style.marginRight = 0;
            UiKit.Fade(b, "border-top-color", "border-bottom-color", "border-left-color", "border-right-color");
            b.RegisterCallback<PointerEnterEvent>(e => UiKit.Borders(b, 1, UiKit.Gold));
            b.RegisterCallback<PointerLeaveEvent>(e => UiKit.Borders(b, 1, UiKit.Border));
            return b;
        }

        // --- text -------------------------------------------------------------------------------------------------------------
        /// A section title with a line under it (FollowPlayer's Section). Other mods: upper-case, NameColor. KydlCore build: the text as written (PascalCase) in TabColor, because a section is a settings tab (D-049).
        public static VisualElement Heading(string text)
        {
            var s = new VisualElement(); s.style.flexShrink = 0; s.style.marginTop = 16; s.style.marginBottom = 6;
            var head = UiKit.Label(text, UiKit.SectionSize, UiKit.TabColor, true);
            head.style.letterSpacing = 1.5f;
            s.Add(head);
            var line = new VisualElement(); line.style.height = 1; line.style.backgroundColor = UiKit.Border; line.style.marginTop = 4;
            s.Add(line);
            return s;
        }

        /// A small dim wrapping text line (hints, explanations).
        public static Label Note(string text)
        {
            var l = UiKit.Label(text, UiKit.SmallSize, UiKit.TextDim);
            l.style.whiteSpace = WhiteSpace.Normal; l.style.flexShrink = 0; l.style.marginTop = 2; l.style.marginBottom = 2;
            return l;
        }

        /// A row: label on the left (fixed width), control on the right.
        public static VisualElement Row(string label, VisualElement control)
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row; r.style.alignItems = Align.Center; r.style.flexShrink = 0;
            r.style.marginTop = 4; r.style.marginBottom = 4;
            var l = UiKit.Label(label, UiKit.NameSize, UiKit.NameColor); l.style.width = 170; l.style.flexShrink = 0;
            r.Add(l);
            control.style.flexGrow = 1; control.style.flexShrink = 1;
            r.Add(control);
            return r;
        }

        /// Puts the items side by side in one wrapping line.
        public static VisualElement Line(params VisualElement[] items)
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row; r.style.flexWrap = Wrap.Wrap; r.style.alignItems = Align.Center; r.style.flexShrink = 0;
            foreach (var i in items) r.Add(i);
            return r;
        }

        // --- text field -----------------------------------------------------------------------------------------------------
        /// A dark single-line text input; `changed` gets the new text on every edit.
        public static TextField TextField(string value, int maxLength, Action<string> changed)
        {
            changed = UiLog.Wrap(changed);       // log lines of the callback belong to the mod whose tab built this control (UiLog.Owner)
            var f = new TextField { value = value ?? "", maxLength = maxLength };
            f.style.marginLeft = 0; f.style.marginRight = 0; f.style.marginTop = 0; f.style.marginBottom = 0; f.style.flexShrink = 0;
            StyleInput(f, UiKit.FontSize);
            f.RegisterValueChangedCallback(e => changed?.Invoke(e.newValue));
            return f;
        }

        static void StyleInput(TextField f, int size)
        {
            var input = f.Q(className: UnityEngine.UIElements.TextField.inputUssClassName) ?? f;
            input.style.backgroundColor = UiKit.Track; input.style.color = UiKit.ValueColor; input.style.fontSize = size;
            UiKit.Borders(input, 1, UiKit.Border); UiKit.Radius(input, 4); UiKit.Pad(input, 5, 8);
            input.style.unityTextAlign = TextAnchor.MiddleLeft;
            f.RegisterCallback<FocusInEvent>(e => UiKit.Borders(input, 1, UiKit.Gold));
            f.RegisterCallback<FocusOutEvent>(e => UiKit.Borders(input, 1, UiKit.Border));
        }

        /// A pointer / key / panel handler that runs as the mod whose tab or window built the control (UiLog.Owner, D-071 / KydlCore D-086): the "Ui: ..."
        /// lines it writes carry that mod's tag and follow its debug switch, and controls it rebuilds (colour sliders) keep the owner.
        static EventCallback<T> Owned<T>(EventCallback<T> handler) where T : EventBase<T>, new()
        {
            var owner = UiLog.Owner;
            // Stryker disable once Boolean : the bool is only the type argument of Within, nobody reads the result
            return e => UiLog.Within<bool>(owner, () => { handler(e); return true; });
        }

        // --- switch -----------------------------------------------------------------------------------------------------------
        /// A pill switch with its label (FollowPlayer's toggle); click anywhere on it.
        public static VisualElement Toggle(string text, bool on, Action<bool> changed)
        {
            changed = UiLog.Wrap(changed);
            bool state = on;
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row; row.style.alignItems = Align.Center; row.style.flexShrink = 0;
            UiKit.Pad(row, 5, 8); UiKit.Radius(row, 4); row.style.marginRight = 6;
            UiKit.Fade(row, "background-color");

            var sw = new VisualElement(); sw.pickingMode = PickingMode.Ignore;
            sw.style.width = 44; sw.style.height = 22; sw.style.flexShrink = 0; sw.style.marginRight = 8;
            UiKit.Radius(sw, 11); UiKit.Borders(sw, 1, UiKit.Border); UiKit.Fade(sw, "background-color");
            var knob = new VisualElement(); knob.pickingMode = PickingMode.Ignore;
            knob.style.position = Position.Absolute; knob.style.top = 2; knob.style.width = 16; knob.style.height = 16;
            UiKit.Radius(knob, 8); UiKit.Fade(knob, "left", "background-color");
            sw.Add(knob);
            row.Add(sw);
            row.Add(UiKit.Label(text, UiKit.NameSize, UiKit.NameColor));

            void Show()
            {
                sw.style.backgroundColor = state ? UiKit.GoldDim : UiKit.Track;
                knob.style.left = state ? 24 : 2;
                knob.style.backgroundColor = state ? UiKit.Gold : UiKit.TextDim;
            }
            row.RegisterCallback<PointerEnterEvent>(e => row.style.backgroundColor = UiKit.PanelLight);
            row.RegisterCallback<PointerLeaveEvent>(e => row.style.backgroundColor = Color.clear);
            row.RegisterCallback(Owned<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                state = !state; Show();
                UiLog.Verbose("Ui: switch '" + text + "' " + (state ? "on" : "off"));
                changed?.Invoke(state);
            }));
            Show();
            return row;
        }

        // --- slider -----------------------------------------------------------------------------------------------------------
        /// A thin slider with a round handle and a number box (type a value, Enter or click elsewhere applies it). `format` formats the value.
        public static VisualElement Slider(float min, float max, float value, string format, Action<float> changed)
        {
            changed = UiLog.Wrap(changed);
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Row; box.style.alignItems = Align.Center; box.style.flexShrink = 0; box.style.minHeight = 34;

            var hit = new VisualElement(); hit.style.flexGrow = 1; hit.style.height = 24; hit.style.justifyContent = Justify.Center; hit.style.marginRight = 10;
            var track = new VisualElement(); track.pickingMode = PickingMode.Ignore; track.style.height = 6;
            UiKit.Radius(track, 3); track.style.backgroundColor = UiKit.Track; UiKit.Borders(track, 1, UiKit.Border);
            hit.Add(track);
            var fill = new VisualElement(); fill.pickingMode = PickingMode.Ignore;
            fill.style.position = Position.Absolute; fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
            fill.style.backgroundColor = UiKit.GoldDim; UiKit.Radius(fill, 3);
            track.Add(fill);
            var handle = new VisualElement(); handle.pickingMode = PickingMode.Ignore;
            handle.style.position = Position.Absolute; handle.style.top = 4; handle.style.width = 16; handle.style.height = 16; handle.style.marginLeft = -8;
            UiKit.Radius(handle, 8); handle.style.backgroundColor = UiKit.Gold; UiKit.Borders(handle, 1, UiKit.OnDark);
            hit.Add(handle);
            box.Add(hit);

            var input = new TextField { maxLength = 8 };
            input.style.width = 80; input.style.flexShrink = 0;
            input.style.marginLeft = 0; input.style.marginRight = 0; input.style.marginTop = 0; input.style.marginBottom = 0;
            StyleInput(input, UiKit.SmallSize + 1);
            box.Add(input);

            float v = Mathf.Clamp(value, min, max);
            bool editing = false;
            void Show()
            {
                float t = max > min ? Mathf.InverseLerp(min, max, v) : 0f;
                fill.style.width = Length.Percent(t * 100f);
                handle.style.left = Length.Percent(t * 100f);
                if (!editing) input.SetValueWithoutNotify(v.ToString(format));
            }
            void Set(float nv, string how) { v = Mathf.Clamp(nv, min, max); Show(); changed?.Invoke(v); }
            void FromX(float x) { float w = hit.resolvedStyle.width; if (w > 1f) Set(Mathf.Lerp(min, max, Mathf.Clamp01(x / w)), "drag"); }
            void Commit()
            {
                float typed;
                string t = (input.value ?? "").Trim().Replace(',', '.');
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out typed)) { Set(typed, "typed"); UiLog.Verbose("Ui: slider typed " + v.ToString(format)); }
                else { UiLog.Verbose("Ui: slider input '" + input.value + "' is not a number, kept " + v.ToString(format)); Show(); }
            }
            hit.RegisterCallback<PointerDownEvent>(e => { hit.CapturePointer(e.pointerId); FromX(e.localPosition.x); e.StopPropagation(); });
            hit.RegisterCallback<PointerMoveEvent>(e => { if (hit.HasPointerCapture(e.pointerId)) FromX(e.localPosition.x); });
            hit.RegisterCallback(Owned<PointerUpEvent>(e => { if (!hit.HasPointerCapture(e.pointerId)) return; hit.ReleasePointer(e.pointerId); UiLog.Verbose("Ui: slider set to " + v.ToString(format)); }));
            input.RegisterCallback<FocusInEvent>(e => editing = true);
            input.RegisterCallback(Owned<FocusOutEvent>(e => { editing = false; Commit(); }));
            input.RegisterCallback(Owned<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation(); editing = false; Commit(); input.Blur();
            }));
            Show();
            return box;
        }

        // --- drop-down --------------------------------------------------------------------------------------------------------
        const float ItemHeight = 40f, ListMax = 400f;

        /// A field that opens a scrolling list of `options` under it. `label` turns an option into its shown text.
        public static Button Dropdown(IList<string> options, string current, Func<string, string> label, Action<string> chosen)
        {
            chosen = UiLog.Wrap(chosen);
            Button b = null;
            string cur = current;
            VisualElement openList = null;   // the list of this field while it is open
            b = Field(label(current) + "  ▾", () =>
            {
                // the list goes into UiKit.Root (it carries the font); the panel's own visualTree has none (T-56R: empty lines in game)
                var root = UiKit.Root;
                if (root == null) return;
                var shade = new VisualElement { name = "kydl-dropdown" };
                shade.style.position = Position.Absolute; shade.style.left = 0; shade.style.top = 0; shade.style.right = 0; shade.style.bottom = 0;
                shade.style.backgroundColor = new Color(0, 0, 0, 0.35f);
                var box = new VisualElement();
                var wb = b.worldBound;
                float h = Mathf.Min(ListMax, options.Count * ItemHeight + 12f);
                float top = wb.yMax + h > root.layout.height ? Mathf.Max(0f, wb.yMin - h) : wb.yMax + 2f;     // opens upwards near the bottom of the screen
                box.style.position = Position.Absolute; box.style.left = wb.xMin; box.style.top = top;
                box.style.width = Mathf.Max(wb.width, 260f); box.style.height = h;
                box.style.backgroundColor = new Color(UiKit.Panel.r, UiKit.Panel.g, UiKit.Panel.b, 1f);   // solid: the menu showed through
                UiKit.Borders(box, 1, UiKit.Gold); UiKit.Radius(box, 6); UiKit.Pad(box, 4, 4);
                var scroll = new UiScroll("drop-down");
                box.Add(scroll.Root);
                foreach (var o in options)
                {
                    string opt = o;
                    var item = UiKit.Label(label(opt), UiKit.FontSize, opt == cur ? UiKit.ValueColor : UiKit.Text);
                    item.pickingMode = PickingMode.Position;
                    item.style.height = ItemHeight - 4f; item.style.unityTextAlign = TextAnchor.MiddleLeft; item.style.flexShrink = 0;
                    UiKit.Pad(item, 0, 10); UiKit.Radius(item, 4); UiKit.Fade(item, "background-color");
                    item.RegisterCallback<PointerEnterEvent>(e => item.style.backgroundColor = UiKit.PanelHover);
                    item.RegisterCallback<PointerLeaveEvent>(e => item.style.backgroundColor = Color.clear);
                    item.RegisterCallback(Owned<PointerDownEvent>(e =>
                    {
                        if (e.button != 0) return;
                        cur = opt; b.text = label(opt) + "  ▾"; shade.RemoveFromHierarchy();
                        UiLog.Verbose("Ui: drop-down chose '" + opt + "'");
                        chosen?.Invoke(opt);
                    }));
                    scroll.Content.Add(item);
                }
                shade.RegisterCallback(Owned<PointerDownEvent>(e => { if (e.target == shade) { shade.RemoveFromHierarchy(); UiLog.Verbose("Ui: drop-down closed without a choice"); } }));
                shade.Add(box);
                root.Add(shade);
                openList = shade;
                int at = options.IndexOf(cur);
                if (at > 3) scroll.ScrollTo((at - 3) * ItemHeight);
                UiLog.Verbose("Ui: drop-down opened with " + options.Count + " option(s), current '" + cur + "'");
            });
            // A list is a sibling of the window in UiKit.Root, not a child of it: when the window that owns this field is removed (Escape, X, a game menu
            // took over) the list must go with it, or it stays on screen over the world (user test 2026-10-24, WhipItUp D-077 F-4). Fixed here, for every mod.
            b.RegisterCallback(Owned<DetachFromPanelEvent>(e =>
            {
                var left = openList;
                openList = null;
                if (left == null || left.parent == null) return;
                left.RemoveFromHierarchy();
                UiLog.Verbose("Ui: drop-down closed because its field left the panel");
            }));
            return b;
        }

        // --- colour -----------------------------------------------------------------------------------------------------------
        static readonly string[] Palette = { "#FFFFFF", "#000000", "#E8D5A9", "#FFB85C", "#C0392B", "#E67E22", "#F1C40F", "#2ECC71", "#1ABC9C", "#00FFFF", "#3498DB", "#9B59B6", "#FF33FF", "#7F8C8D" };

        /// FollowPlayer's colour editor: preview bar, preset swatches, Red / Green / Blue sliders, hex box. `changed` gets "#RRGGBB".
        public static VisualElement ColorPicker(string hex, Action<string> changed, out Action<string> set)
        {
            changed = UiLog.Wrap(changed);
            var box = new VisualElement(); box.style.flexShrink = 0;
            Color c;
            if (!ColorUtility.TryParseHtmlString(hex ?? "#FFFFFF", out c)) c = Color.white;
            var preview = new VisualElement(); preview.style.height = 26; UiKit.Radius(preview, 4); UiKit.Borders(preview, 1, UiKit.Border);
            box.Add(preview);

            var pal = new VisualElement(); pal.style.flexDirection = FlexDirection.Row; pal.style.flexWrap = Wrap.Wrap; pal.style.flexShrink = 0;
            pal.style.marginTop = 8; pal.style.marginBottom = 4;
            box.Add(pal);
            var sliders = new VisualElement(); sliders.style.flexShrink = 0;
            box.Add(sliders);
            TextField field = null;

            void Apply(bool fromField)
            {
                string h = "#" + ColorUtility.ToHtmlStringRGB(c);
                preview.style.backgroundColor = c;
                // T-65 (user: the Red / Green / Blue sliders did not slide like the spacing one): `field.value = h` inside a drag sends its change
                // event LATER (UI Toolkit queues events sent while one is handled), so the hex box rebuilt the sliders
                // and the slider being dragged was thrown away after the first step. SetValueWithoutNotify sends nothing.
                if (!fromField && field != null) field.SetValueWithoutNotify(h);
                changed?.Invoke(h);
            }
            void RebuildSliders()
            {
                sliders.Clear();
                sliders.Add(Row("Red", Slider(0, 255, c.r * 255f, "0", v => { c.r = v / 255f; Apply(false); })));
                sliders.Add(Row("Green", Slider(0, 255, c.g * 255f, "0", v => { c.g = v / 255f; Apply(false); })));
                sliders.Add(Row("Blue", Slider(0, 255, c.b * 255f, "0", v => { c.b = v / 255f; Apply(false); })));
            }
            foreach (var p in Palette)
            {
                Color pc; ColorUtility.TryParseHtmlString(p, out pc);
                var s = new VisualElement(); s.style.width = 28; s.style.height = 28; s.style.marginRight = 6; s.style.marginTop = 4;
                s.style.backgroundColor = pc; UiKit.Radius(s, 4); UiKit.Borders(s, 1, UiKit.Border);
                s.RegisterCallback<PointerEnterEvent>(e => UiKit.Borders(s, 2, UiKit.Gold));
                s.RegisterCallback<PointerLeaveEvent>(e => UiKit.Borders(s, 1, UiKit.Border));
                s.RegisterCallback(Owned<PointerDownEvent>(e => { c = pc; RebuildSliders(); Apply(false); }));
                pal.Add(s);
            }
            field = TextField("#" + ColorUtility.ToHtmlStringRGB(c), 7, v =>
            {
                Color p;
                if (v.TrimStart('#').Length != 6 || !ColorUtility.TryParseHtmlString(v.StartsWith("#") ? v : "#" + v, out p)) return;
                if (ColorUtility.ToHtmlStringRGB(p) == ColorUtility.ToHtmlStringRGB(c)) { UiLog.Verbose("Ui: colour hex " + v + " = current colour, sliders kept"); return; }
                c = p; RebuildSliders(); Apply(true);
                UiLog.Verbose("Ui: colour typed " + v + ", sliders set");
            });
            field.style.width = 130; field.style.flexGrow = 0;
            var hexRow = Row("Hex", field); field.style.flexGrow = 0;
            box.Add(hexRow);
            RebuildSliders();
            preview.style.backgroundColor = c;
            set = h => { Color p; if (ColorUtility.TryParseHtmlString(h, out p)) { c = p; RebuildSliders(); field.SetValueWithoutNotify(h); preview.style.backgroundColor = c; } };
            return box;
        }
    }
}
