using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kydl.Ui
{
    /// <summary>
    /// SHARED (added 2026-10-26 for ChestDecor). The same thin slider as UiControls.Slider (round handle, typed number box), but as an object
    /// the menu can change later: SetValueWithoutNotify, SetRange (min / max move with the selection), Enabled, and optional clickable MARKS
    /// (dots under the track at fractions 0..1: a click puts the handle there). UiControls.Slider is unchanged for the older menus.
    /// </summary>
#if KYDL_CORE_PUBLIC
    public sealed class UiSlider
#else
    internal sealed class UiSlider
#endif
    {
        /// <summary>The slider element to add to a parent.</summary>
        public readonly VisualElement Root;
        readonly VisualElement _hit, _fill, _handle, _marks;
        readonly TextField _input;
        readonly string _format;
        readonly Action<float> _changed;
        float _min, _max, _value;
        bool _editing, _enabled = true;

        /// <summary>The current value.</summary>
        public float Value { get { return _value; } }
        /// <summary>The lowest value.</summary>
        public float Min { get { return _min; } }
        /// <summary>The highest value.</summary>
        public float Max { get { return _max; } }

        /// <summary>A slider from `min` to `max` at `value`; `format` is the number format of the typed box; `changed` gets every change made by the user; `marks` = dots under the track at fractions 0..1.</summary>
        public UiSlider(float min, float max, float value, string format, Action<float> changed, IList<float> marks = null)
        {
            _format = format; _changed = changed;
            _min = Mathf.Min(min, max); _max = Mathf.Max(min, max);
            Root = new VisualElement();
            Root.style.flexDirection = FlexDirection.Row; Root.style.alignItems = Align.Center; Root.style.flexShrink = 0; Root.style.minHeight = 34;

            var left = new VisualElement(); left.style.flexGrow = 1; left.style.marginRight = 10;
            _hit = new VisualElement(); _hit.style.height = 24; _hit.style.justifyContent = Justify.Center;
            var track = new VisualElement(); track.pickingMode = PickingMode.Ignore; track.style.height = 6;
            UiKit.Radius(track, 3); track.style.backgroundColor = UiKit.Track; UiKit.Borders(track, 1, UiKit.Border);
            _hit.Add(track);
            _fill = new VisualElement(); _fill.pickingMode = PickingMode.Ignore;
            _fill.style.position = Position.Absolute; _fill.style.left = 0; _fill.style.top = 0; _fill.style.bottom = 0;
            _fill.style.backgroundColor = UiKit.GoldDim; UiKit.Radius(_fill, 3);
            track.Add(_fill);
            _handle = new VisualElement(); _handle.pickingMode = PickingMode.Ignore;
            _handle.style.position = Position.Absolute; _handle.style.top = 4; _handle.style.width = 16; _handle.style.height = 16; _handle.style.marginLeft = -8;
            UiKit.Radius(_handle, 8); _handle.style.backgroundColor = UiKit.Gold; UiKit.Borders(_handle, 1, UiKit.OnDark);
            _hit.Add(_handle);
            left.Add(_hit);
            _marks = new VisualElement(); _marks.style.height = marks != null && marks.Count > 0 ? 16 : 0; _marks.style.flexShrink = 0;
            left.Add(_marks);
            Root.Add(left);
            if (marks != null) foreach (var f in marks) AddMark(Mathf.Clamp01(f));

            _input = new TextField { maxLength = 8 };
            _input.style.width = 90; _input.style.flexShrink = 0;
            _input.style.marginLeft = 0; _input.style.marginRight = 0; _input.style.marginTop = 0; _input.style.marginBottom = 0;
            var inner = _input.Q(className: TextField.inputUssClassName) ?? _input;
            inner.style.backgroundColor = UiKit.Track; inner.style.color = UiKit.ValueColor; inner.style.fontSize = UiKit.SmallSize + 1;
            UiKit.Borders(inner, 1, UiKit.Border); UiKit.Radius(inner, 4); UiKit.Pad(inner, 5, 8);
            _input.RegisterCallback<FocusInEvent>(e => { _editing = true; UiKit.Borders(inner, 1, UiKit.Gold); });
            _input.RegisterCallback<FocusOutEvent>(e => { _editing = false; UiKit.Borders(inner, 1, UiKit.Border); Commit(); });
            _input.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation(); _editing = false; Commit(); _input.Blur();
            });
            Root.Add(_input);

            _hit.RegisterCallback<PointerDownEvent>(e => { if (!_enabled) return; _hit.CapturePointer(e.pointerId); FromX(e.localPosition.x); e.StopPropagation(); });
            _hit.RegisterCallback<PointerMoveEvent>(e => { if (_hit.HasPointerCapture(e.pointerId)) FromX(e.localPosition.x); });
            _hit.RegisterCallback<PointerUpEvent>(e => { if (!_hit.HasPointerCapture(e.pointerId)) return; _hit.ReleasePointer(e.pointerId); UiLog.Verbose("UiSlider: set to " + Text(_value)); });
            _value = Mathf.Clamp(value, _min, _max);
            Show();
        }

        void AddMark(float fraction)
        {
            var hit = new VisualElement();
            hit.style.position = Position.Absolute; hit.style.top = 0; hit.style.width = 20; hit.style.height = 16; hit.style.marginLeft = -10;
            hit.style.left = Length.Percent(fraction * 100f); hit.style.alignItems = Align.Center; hit.style.justifyContent = Justify.Center;
            var dot = new VisualElement(); dot.pickingMode = PickingMode.Ignore; dot.style.width = 8; dot.style.height = 8;
            UiKit.Radius(dot, 4); dot.style.backgroundColor = UiKit.TextDim;
            hit.Add(dot);
            hit.RegisterCallback<PointerEnterEvent>(e => dot.style.backgroundColor = UiKit.Gold);
            hit.RegisterCallback<PointerLeaveEvent>(e => dot.style.backgroundColor = UiKit.TextDim);
            hit.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || !_enabled) return;
                e.StopPropagation();
                UiLog.Verbose("UiSlider: mark " + (fraction * 100f).ToString("0", CultureInfo.InvariantCulture) + "% clicked");
                Set(Mathf.Lerp(_min, _max, fraction));
            });
            _marks.Add(hit);
        }

        string Text(float v) { return v.ToString(_format, CultureInfo.InvariantCulture); }

        void Show()
        {
            float t = _max > _min ? Mathf.InverseLerp(_min, _max, _value) : 0f;
            _fill.style.width = Length.Percent(t * 100f);
            _handle.style.left = Length.Percent(t * 100f);
            if (!_editing) _input.SetValueWithoutNotify(Text(_value));
        }

        void Set(float v)
        {
            float nv = Mathf.Clamp(v, _min, _max);
            if (Mathf.Approximately(nv, _value)) { Show(); return; }
            _value = nv; Show();
            _changed?.Invoke(_value);
        }

        void FromX(float x) { float w = _hit.resolvedStyle.width; if (w > 1f) Set(Mathf.Lerp(_min, _max, Mathf.Clamp01(x / w))); }

        void Commit()
        {
            float typed;
            string t = (_input.value ?? "").Trim().Replace(',', '.').TrimEnd('m', 'x', '°', ' ');
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out typed)) { Set(typed); UiLog.Verbose("UiSlider: typed " + Text(_value)); }
            else { UiLog.Verbose("UiSlider: input '" + _input.value + "' is not a number, kept " + Text(_value)); Show(); }
        }

        /// Puts the handle on `v` (clamped) without calling the change callback. Expected outcome: Value equals the clamped v.
        public bool SetValueWithoutNotify(float v)
        {
            _value = Mathf.Clamp(v, _min, _max);
            Show();
            return Mathf.Approximately(_value, Mathf.Clamp(v, _min, _max));
        }

        /// New range; the value is clamped into it (no callback). Expected outcome: Min <= Value <= Max.
        public bool SetRange(float min, float max)
        {
            _min = Mathf.Min(min, max); _max = Mathf.Max(min, max);
            _value = Mathf.Clamp(_value, _min, _max);
            Show();
            return _value >= _min && _value <= _max;
        }

        /// Greyed out and not draggable while false.
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; Root.style.opacity = value ? 1f : 0.45f; _input.isReadOnly = !value; }
        }
    }
}
