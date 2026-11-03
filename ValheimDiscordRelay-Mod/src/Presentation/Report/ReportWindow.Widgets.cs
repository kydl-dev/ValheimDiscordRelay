using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Report
{
    public sealed partial class ReportWindow
    {
        // ---------------------------------------------------------------- button

        /// <summary>
        /// A UI Toolkit Button. Its colours (normal, :hover, :active, :disabled) come from the style sheet; only the behaviour
        /// is set here: it is clicked with the mouse only (no keyboard focus), on one line.
        /// </summary>
        private sealed class Btn
        {
            public readonly Button Element;

            private bool _enabled = true;

            public Btn(Action onClick, params string[] classes)
            {
                Element = new Button();
                ResetBox(Element);

                foreach (string cssClass in classes)
                    Element.AddToClassList(cssClass);

                IStyle s = Element.style;
                s.whiteSpace = WhiteSpace.NoWrap;
                s.overflow = Overflow.Hidden;
                s.textOverflow = TextOverflow.Clip;

                // No keyboard focus: Space or Enter must not "click" a button the mouse used last.
                Element.focusable = false;
                Element.clicked += onClick;
            }

            /// <summary>Enables or disables the button. True when that changed it (the caller then restyles the button for :disabled).</summary>
            public bool SetEnabled(bool enabled)
            {
                if (_enabled == enabled)
                    return false;

                _enabled = enabled;
                Element.SetEnabled(enabled);
                return true;
            }
        }

        // ---------------------------------------------------------------- scrolling list

        /// <summary>
        /// A vertical scrolling area with a scrollbar, built from plain elements.
        ///
        /// UI Toolkit's own ScrollView is not used on purpose: its layout (and its scrollbar, a slider with a dragger) is
        /// defined by the runtime theme style sheet, which this window does not have. This does the little that is needed:
        /// the content sits in a clipping viewport and is moved with its top offset; the mouse wheel moves it three rows at a
        /// time; the thumb can be dragged and a click on the track pages up or down. Track and thumb are styled with the
        /// classes vdr-scroll-track and vdr-scroll-thumb.
        /// </summary>
        private sealed class ScrollPane : VisualElement
        {
            private const float WheelStep = 3f * RowHeight;
            private const float MinThumb = 24f;

            /// <summary>Add the rows here.</summary>
            public readonly VisualElement Content;

            private readonly VisualElement _viewport;
            private readonly VisualElement _track;
            private readonly VisualElement _thumb;

            private float _offset;
            private bool _thumbDown;
            private float _downPointerY;
            private float _downOffset;

            public ScrollPane()
            {
                ResetBox(this);
                style.flexDirection = FlexDirection.Row;

                _viewport = new VisualElement();
                ResetBox(_viewport);
                _viewport.style.flexGrow = 1f;
                _viewport.style.flexShrink = 1f;
                _viewport.style.minWidth = 0f;
                _viewport.style.overflow = Overflow.Hidden;

                // Absolutely positioned and without a height: it is as tall as its rows, whatever the viewport's height is.
                Content = new VisualElement();
                ResetBox(Content);
                Content.style.position = Position.Absolute;
                Content.style.left = 0f;
                Content.style.right = 0f;
                Content.style.top = 0f;
                Content.style.flexDirection = FlexDirection.Column;
                _viewport.Add(Content);

                _track = new VisualElement();
                _track.AddToClassList("vdr-scroll-track");
                ResetBox(_track);
                _track.style.width = ScrollbarWidth;
                _track.style.flexShrink = 0f;

                _thumb = new VisualElement();
                _thumb.AddToClassList("vdr-scroll-thumb");
                ResetBox(_thumb);
                _thumb.style.position = Position.Absolute;
                _thumb.style.left = 2f;
                _thumb.style.right = 2f;
                _thumb.style.top = 0f;
                _thumb.style.height = MinThumb;
                _thumb.style.display = DisplayStyle.None;
                _track.Add(_thumb);

                Add(_viewport);
                Add(_track);

                RegisterCallback<WheelEvent>(OnWheel);
                _viewport.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });
                Content.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });
                _track.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });

                _track.RegisterCallback<PointerDownEvent>(OnTrackPointerDown);
                _thumb.RegisterCallback<PointerDownEvent>(OnThumbPointerDown);
                _thumb.RegisterCallback<PointerMoveEvent>(OnThumbPointerMove);
                _thumb.RegisterCallback<PointerUpEvent>(OnThumbPointerUp);
                _thumb.RegisterCallback<PointerCaptureOutEvent>(delegate { _thumbDown = false; });
            }

            /// <summary>How far the content is scrolled (pixels from the top). Kept within what the content allows.</summary>
            public float Offset
            {
                get { return _offset; }
                set
                {
                    _offset = Mathf.Max(0f, value);
                    Refresh();
                }
            }

            private void OnWheel(WheelEvent evt)
            {
                // Only the direction counts: how big a "step" is differs between mice, touchpads and input back ends.
                float direction = Mathf.Clamp(evt.delta.y, -1f, 1f);
                if (Mathf.Abs(direction) < 0.001f)
                    return;

                Offset = _offset + direction * WheelStep;
                evt.StopPropagation();
            }

            private void OnTrackPointerDown(PointerDownEvent evt)
            {
                // Clicks on the thumb are the thumb's; this is the empty track above or below it.
                if (evt.button != 0 || evt.target != _track)
                    return;

                float page = Mathf.Max(RowHeight, _viewport.layout.height * 0.9f);
                float clickY = ((Vector3)evt.localPosition).y;
                Offset = _offset + (clickY < _thumb.layout.y ? -page : page);
                evt.StopPropagation();
            }

            private void OnThumbPointerDown(PointerDownEvent evt)
            {
                if (evt.button != 0)
                    return;

                _thumbDown = true;
                _downPointerY = ((Vector3)evt.position).y;
                _downOffset = _offset;
                _thumb.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }

            private void OnThumbPointerMove(PointerMoveEvent evt)
            {
                if (!_thumbDown || !_thumb.HasPointerCapture(evt.pointerId))
                    return;

                float travel = _track.layout.height - _thumb.layout.height;
                float max = Content.layout.height - _viewport.layout.height;
                if (float.IsNaN(travel) || float.IsNaN(max) || travel <= 0.5f || max <= 0.5f)
                    return;

                float dy = ((Vector3)evt.position).y - _downPointerY;
                Offset = _downOffset + dy * max / travel;
                evt.StopPropagation();
            }

            private void OnThumbPointerUp(PointerUpEvent evt)
            {
                if (_thumb.HasPointerCapture(evt.pointerId))
                    _thumb.ReleasePointer(evt.pointerId);

                _thumbDown = false;
            }

            /// <summary>
            /// Clamps the offset to the content that is really there, moves the content and positions the thumb. Runs when the
            /// offset changes and whenever the viewport, the content or the track changes size.
            /// </summary>
            private void Refresh()
            {
                float viewHeight = _viewport.layout.height;
                float contentHeight = Content.layout.height;
                bool known = !float.IsNaN(viewHeight) && !float.IsNaN(contentHeight) && viewHeight > 0f;

                float max = known ? Mathf.Max(0f, contentHeight - viewHeight) : float.MaxValue;
                if (_offset > max)
                    _offset = max;

                Content.style.top = -_offset;

                if (!known || max <= 0.5f)
                {
                    _thumb.style.display = DisplayStyle.None;
                    return;
                }

                float trackHeight = _track.layout.height;
                if (float.IsNaN(trackHeight) || trackHeight <= 0f)
                    trackHeight = viewHeight;

                float thumbHeight = Mathf.Clamp(trackHeight * viewHeight / contentHeight, MinThumb, trackHeight);

                _thumb.style.display = DisplayStyle.Flex;
                _thumb.style.height = thumbHeight;
                _thumb.style.top = (trackHeight - thumbHeight) * (_offset / max);
            }
        }
    }
}
