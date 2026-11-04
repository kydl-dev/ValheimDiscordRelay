using System;
using System.Globalization;
using KydlCore.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// A fixed-size viewport with one model in 3D (IModelScene renders it into a texture; from WhipItUp's WeaponViewportView, shared through the
    /// mod template). Left-drag turns it, right-drag moves it inside the view, the mouse wheel and [+] / [-] zoom, [Reset] returns to the default
    /// view. Zoomed in, the model simply runs past the edges. Without a scene (null) the frame only shows the title and the `unavailable` note.
    /// </summary>
    internal static class ModelViewportView
    {
        /// `title` = shown top-left, `modelId` = what makeScene's rig shows, `hint` / `reset` / `unavailable` = the texts (the caller localizes).
        internal static VisualElement Build(string title, string modelId, Func<IModelScene> makeScene, ViewerSettings settings, string hint, string reset, string unavailable)
        {
            settings = settings ?? new ViewerSettings();
            settings.Clamp(UiLog.Verbose);
            var frame = new VisualElement { name = "kydl-viewport" };
            frame.style.flexShrink = 0; frame.style.height = settings.FrameHeight; frame.style.marginTop = 10;
            frame.style.backgroundColor = UiKit.Track;
            UiKit.Borders(frame, 1, UiKit.Border); UiKit.Radius(frame, 4);
            frame.style.overflow = Overflow.Hidden;                    // what is zoomed past the frame is never shown

            IModelScene scene = null;
            try { scene = makeScene != null ? makeScene() : null; }
            catch (Exception e) { UiLog.Error("ModelViewportView: making the 3D scene failed: " + e.GetType().Name + ": " + e.Message); }
            if (scene == null)
            {
                frame.style.justifyContent = Justify.Center; frame.style.alignItems = Align.Center;
                frame.Add(UiKit.Label(title ?? "", UiKit.SectionSize, UiKit.TextDim, true));
                frame.Add(UiKit.Label(unavailable ?? "", UiKit.FontSize, UiKit.TextDim));
                UiLog.Verbose("ModelViewportView: no 3D scene, showing the title '" + title + "' only");
                return frame;
            }
            _ = new Viewport(title, modelId, scene, settings, frame, hint, reset);
            return frame;
        }

        /// The live viewport: owns the scene (disposed when the window goes away), the orbit and the input.
        sealed class Viewport
        {
            readonly IModelScene _scene;
            readonly ViewerOrbit _orbit;
            readonly Image _image;
            readonly VisualElement _frame;
            readonly string _modelId;
            bool _dragging;
            bool _panning;                                              // the drag in progress: right button = pan, left button = rotate
            int _dragButton = -1;
            Vector2 _last;
            int _pointer = -1;

            internal Viewport(string title, string modelId, IModelScene scene, ViewerSettings settings, VisualElement frame, string hint, string reset)
            {
                _scene = scene;
                _frame = frame;
                _modelId = modelId;
                _orbit = new ViewerOrbit(settings, UiLog.Verbose);

                _image = new Image { name = "kydl-viewport-image", scaleMode = ScaleMode.StretchToFill };
                _image.style.position = Position.Absolute;
                _image.style.left = 0; _image.style.top = 0; _image.style.right = 0; _image.style.bottom = 0;
                frame.Add(_image);

                var name = UiKit.Label(title ?? "", UiKit.SectionSize, UiKit.Gold, true);
                name.style.position = Position.Absolute; name.style.left = 10; name.style.top = 6;
                name.pickingMode = PickingMode.Ignore;
                frame.Add(name);

                var hintLabel = UiKit.Label(hint ?? "", UiKit.SmallSize, UiKit.TextDim);
                hintLabel.style.position = Position.Absolute; hintLabel.style.left = 10; hintLabel.style.bottom = 8;
                hintLabel.pickingMode = PickingMode.Ignore;
                frame.Add(hintLabel);

                var buttons = new VisualElement { name = "kydl-viewport-buttons" };
                buttons.style.position = Position.Absolute; buttons.style.right = 6; buttons.style.bottom = 4;
                buttons.style.flexDirection = FlexDirection.Row;
                buttons.Add(UiControls.Button("+", () => Changed(_orbit.StepZoom(1f), "zoom in button")));
                buttons.Add(UiControls.Button("-", () => Changed(_orbit.StepZoom(-1f), "zoom out button")));
                buttons.Add(UiControls.Button(reset ?? "Reset", () => Changed(_orbit.Reset(), "reset button")));
                frame.Add(buttons);

                _image.RegisterCallback<PointerDownEvent>(OnDown);
                _image.RegisterCallback<PointerMoveEvent>(OnMove);
                _image.RegisterCallback<PointerUpEvent>(OnUp);
                _image.RegisterCallback<PointerCaptureOutEvent>(e => EndDrag("capture lost"));
                frame.RegisterCallback<WheelEvent>(OnWheel);
                frame.RegisterCallback<GeometryChangedEvent>(e => OnResized(frame));
                frame.RegisterCallback<DetachFromPanelEvent>(e => Teardown());

                bool shown = _scene.Show(_modelId);
                _orbit.Reset();
                UiLog.Verbose("ModelViewportView: 3D viewport built for '" + _modelId + "', model " + (shown ? "shown" : "NOT shown (see the ModelViewerRig lines above)") + " (" + settings + ")");
            }

            void OnDown(PointerDownEvent e)
            {
                if (e.button != 0 && e.button != 1) return;                // left = rotate, right = pan, other buttons are not ours
                if (_dragging)
                {
                    UiLog.Verbose("ModelViewportView: button " + e.button + " pressed during a " + (_panning ? "pan" : "rotate") + " drag (button " + _dragButton + ") - ignored");
                    return;
                }
                _dragging = true;
                _panning = e.button == 1;
                _dragButton = e.button;
                _pointer = e.pointerId;
                _last = new Vector2(e.position.x, e.position.y);
                _image.CapturePointer(e.pointerId);
                e.StopPropagation();
                UiLog.Verbose("ModelViewportView: " + (_panning ? "pan" : "rotate") + " drag started at " + P(_last) + " (button " + e.button + ", pointer " + e.pointerId + ", frame " + FrameHeightPx().ToString("0", CultureInfo.InvariantCulture) + " UI px high)");
            }

            void OnMove(PointerMoveEvent e)
            {
                if (!_dragging || e.pointerId != _pointer) return;
                var now = new Vector2(e.position.x, e.position.y);
                var d = now - _last;
                _last = now;
                if (d.sqrMagnitude <= 0f) return;
                bool changed = _panning ? _orbit.Pan(d.x, d.y, FrameHeightPx()) : _orbit.Drag(d.x, d.y);
                if (changed) Draw(null);
                e.StopPropagation();
            }

            void OnUp(PointerUpEvent e)
            {
                if (e.button != _dragButton || e.pointerId != _pointer) return;
                if (_image.HasPointerCapture(e.pointerId)) _image.ReleasePointer(e.pointerId);
                EndDrag("button " + e.button + " released");
                e.StopPropagation();
            }

            void EndDrag(string why)
            {
                if (!_dragging) return;
                _dragging = false;
                _pointer = -1;
                UiLog.Verbose("ModelViewportView: " + (_panning ? "pan" : "rotate") + " drag ended (" + why + "), view " + _orbit);
                _panning = false;
                _dragButton = -1;
            }

            // The frame's height in UI pixels: what a dragged pixel is measured against, so the model follows the mouse.
            float FrameHeightPx() { return _frame.contentRect.height; }

            void OnWheel(WheelEvent e)
            {
                Changed(_orbit.Wheel(e.delta.y), "wheel " + e.delta.y.ToString("0.##", CultureInfo.InvariantCulture));
                e.StopPropagation();                                      // the window's scroll view must not scroll while zooming
            }

            void OnResized(VisualElement frame)
            {
                var r = frame.contentRect;
                float scale = PixelsPerPoint();
                int w = Mathf.RoundToInt(r.width * scale), h = Mathf.RoundToInt(r.height * scale);
                if (w < 16 || h < 16) { UiLog.Verbose("ModelViewportView: frame " + r.width + "x" + r.height + " UI px is too small (hidden tab?), no texture"); return; }
                UiLog.Verbose("ModelViewportView: frame " + r.width.ToString("0", CultureInfo.InvariantCulture) + "x" + r.height.ToString("0", CultureInfo.InvariantCulture)
                    + " UI px x " + scale.ToString("0.###", CultureInfo.InvariantCulture) + " = " + w + "x" + h + " screen px");
                if (_scene.Resize(w, h)) _image.image = _scene.Texture;
                Draw("resized");
            }

            // UI points -> screen pixels of the shared panel (scaled from 1080 p by height, KydlCore.Ui UiKit).
            static float PixelsPerPoint()
            {
                var s = UiKit.Settings;
                if (s == null || s.scaleMode != PanelScaleMode.ScaleWithScreenSize || s.referenceResolution.y <= 0) return 1f;
                float height = s.targetTexture != null ? s.targetTexture.height : Screen.height;
                return Mathf.Clamp(height / s.referenceResolution.y, 0.25f, 8f);
            }

            void Changed(bool changed, string why)
            {
                if (changed) Draw(why);
                else UiLog.Verbose("ModelViewportView: " + why + " - view unchanged (" + _orbit + ")");
            }

            // `why` null = a drag step (the scene logs those throttled).
            void Draw(string why)
            {
                bool done = _scene.Render(_orbit.Yaw, _orbit.Pitch, _orbit.Zoom, _orbit.PanX, _orbit.PanY, _orbit.PanZ);
                _image.MarkDirtyRepaint();
                if (why != null) UiLog.Verbose("ModelViewportView: drawn after " + why + " (" + _orbit + ")" + (done ? "" : " - scene did not render"));
            }

            void Teardown()
            {
                _image.image = null;
                _scene.Dispose();
                UiLog.Verbose("ModelViewportView: viewport closed, 3D scene of '" + _modelId + "' disposed");
            }

            static string P(Vector2 v) { return "(" + v.x.ToString("0", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0", CultureInfo.InvariantCulture) + ")"; }
        }
    }
}
