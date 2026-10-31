using UnityEngine;
using UnityEngine.UIElements;

namespace Kydl.Ui
{
    /// <summary>
    /// T-56R (user: "add a scroll bar so we can see all options"; the font / alignment lists showed empty lines): a scroll area made by hand.
    /// Unity's ScrollView needs the default theme for its scrollbar and its content layout; a game build has no theme, so its children were
    /// squeezed into the window (the empty lines) and no bar was drawn. Here the content is an absolute child that is never squeezed, moved by
    /// the mouse wheel or the bar on the right (drag the handle or click the track).
    /// </summary>
#if KYDL_CORE_PUBLIC
    public sealed class UiScroll
#else
    internal sealed class UiScroll
#endif
    {
        const float BarWidth = 8f, WheelStep = 60f, MinThumb = 28f;

        /// <summary>The scroll area's root: add this to the parent element.</summary>
        public readonly VisualElement Root;
        /// <summary>The scrolled content: add the items that should scroll here.</summary>
        public readonly VisualElement Content;
        readonly VisualElement _viewport, _bar, _thumb;
        readonly string _name;
        float _offset;

        /// <summary>Makes a scroll area; `name` only labels its log lines.</summary>
        public UiScroll(string name)
        {
            _name = name;
            Root = new VisualElement { name = "kydl-scroll", userData = this };
            Root.style.flexDirection = FlexDirection.Row;
            Root.style.flexGrow = 1; Root.style.flexShrink = 1; Root.style.minHeight = 40;

            _viewport = new VisualElement();
            _viewport.style.flexGrow = 1; _viewport.style.flexShrink = 1;
            _viewport.style.overflow = Overflow.Hidden;
            Root.Add(_viewport);

            Content = new VisualElement();
            Content.style.position = Position.Absolute;
            Content.style.left = 0; Content.style.right = 0; Content.style.top = 0;
            Content.style.flexDirection = FlexDirection.Column;
            _viewport.Add(Content);

            // T-58: FollowPlayer's slim bar - dark track, gold handle
            _bar = new VisualElement();
            _bar.style.width = BarWidth; _bar.style.flexShrink = 0; _bar.style.marginLeft = 4; _bar.style.marginRight = 4;
            _bar.style.marginTop = 6; _bar.style.marginBottom = 6;
            _bar.style.backgroundColor = UiKit.Track; UiKit.Radius(_bar, BarWidth / 2f);
            _thumb = new VisualElement();
            _thumb.style.position = Position.Absolute; _thumb.style.left = 0; _thumb.style.right = 0; _thumb.style.top = 0; _thumb.style.height = MinThumb;
            _thumb.style.backgroundColor = UiKit.GoldDim; UiKit.Radius(_thumb, BarWidth / 2f);
            _thumb.RegisterCallback<PointerEnterEvent>(e => _thumb.style.backgroundColor = UiKit.Gold);
            _thumb.RegisterCallback<PointerLeaveEvent>(e => _thumb.style.backgroundColor = UiKit.GoldDim);
            _bar.Add(_thumb);
            Root.Add(_bar);

            _viewport.RegisterCallback<GeometryChangedEvent>(e => Clamp());
            Content.RegisterCallback<GeometryChangedEvent>(e => Clamp());
            Root.RegisterCallback<WheelEvent>(e => { ScrollBy(e.delta.y * WheelStep / 3f); e.StopPropagation(); });
            Drag();
        }

        float ContentH { get { return Content.layout.height; } }
        float ViewH { get { return _viewport.layout.height; } }
        float Max { get { float m = ContentH - ViewH; return m > 0f && !float.IsNaN(m) ? m : 0f; } }

        /// <summary>Scrolls by d px (positive = down); the offset stays inside the content.</summary>
        public void ScrollBy(float d) { _offset += d; Clamp(); }
        /// <summary>Scrolls to y px from the top; the offset stays inside the content.</summary>
        public void ScrollTo(float y) { _offset = y; Clamp(); }

        /// Keeps the offset in range, moves the content, sizes and places the handle; the bar hides when everything fits.
        void Clamp()
        {
            float max = Max;
            _offset = Mathf.Clamp(_offset, 0f, max);
            Content.style.top = -_offset;
            bool needed = max > 0.5f;
            _bar.style.display = needed ? DisplayStyle.Flex : DisplayStyle.None;
            if (!needed) return;
            float track = _bar.layout.height;
            if (float.IsNaN(track) || track <= 0f) return;
            float h = Mathf.Max(MinThumb, track * ViewH / Mathf.Max(1f, ContentH));
            _thumb.style.height = h;
            _thumb.style.top = (track - h) * (_offset / max);
        }

        void Drag()
        {
            bool dragging = false; float startY = 0f, startOffset = 0f;
            _thumb.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                dragging = true; startY = e.position.y; startOffset = _offset;
                _thumb.CapturePointer(e.pointerId); e.StopPropagation();
            });
            _thumb.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                float track = _bar.layout.height - _thumb.layout.height;
                if (track > 1f) ScrollTo(startOffset + (e.position.y - startY) / track * Max);
            });
            _thumb.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false; _thumb.ReleasePointer(e.pointerId);
                UiLog.Verbose("UiScroll '" + _name + "': dragged to " + _offset.ToString("0") + " of " + Max.ToString("0"));
            });
            // a click on the track pages up / down
            _bar.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target != _bar) return;
                ScrollBy(e.localPosition.y < _thumb.layout.y ? -ViewH * 0.9f : ViewH * 0.9f);
            });
        }
    }
}
