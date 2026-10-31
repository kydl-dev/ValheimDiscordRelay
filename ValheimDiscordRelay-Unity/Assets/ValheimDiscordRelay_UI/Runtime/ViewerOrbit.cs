using System;
using System.Globalization;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// Where the viewer camera looks from: yaw, pitch and zoom (1 = the whole model fits) plus a pan offset, changed by drag / right-drag / wheel / buttons and put back by Reset.
    /// Pure logic, no Unity / Valheim types (unit-tested: ViewerOrbitTests). Every change method returns whether the view changed and logs through
    /// the injected `log` (the viewport passes UiLog.Verbose; drags are logged by the scene's throttled render line, not here, to keep the log short).
    /// </summary>
    internal sealed class ViewerOrbit
    {
        readonly ViewerSettings _settings;
        const float Tiny = 1e-6f;                                       // a pan smaller than this (view heights) is rounding, not a move
        readonly Action<string> _log;

        internal float Yaw { get; private set; }
        internal float Pitch { get; private set; }
        internal float Zoom { get; private set; }

        /// Where the camera looks, as an offset from the model's centre in world axes, in "view heights at zoom 1" (the scene turns it into metres).
        /// Fixed in the world, so turning or zooming the view keeps the point you moved to in the middle.
        internal float PanX { get; private set; }
        internal float PanY { get; private set; }
        internal float PanZ { get; private set; }

        internal ViewerOrbit(ViewerSettings settings, Action<string> log = null)
        {
            _settings = settings ?? new ViewerSettings();
            _log = log;
            Reset();
        }

        /// Back to the default view (start yaw / pitch, zoom 1, no pan). Expected outcome: returns whether anything changed.
        internal bool Reset()
        {
            float yaw = ViewerMath.WrapYaw(_settings.StartYaw);
            float pitch = ViewerMath.ClampPitch(_settings.StartPitch, _settings.PitchLimit);
            bool panned = PanX != 0f || PanY != 0f || PanZ != 0f;
            bool changed = yaw != Yaw || pitch != Pitch || Zoom != 1f || panned;
            Yaw = yaw; Pitch = pitch; Zoom = 1f;
            PanX = 0f; PanY = 0f; PanZ = 0f;
            Log("ViewerOrbit: reset to " + this + (changed ? "" : " (already there)"));
            return changed;
        }

        /// A right-drag of dx / dy UI pixels (dx > 0 = right, dy > 0 = down) in a viewport `frameHeightPx` UI pixels high: the model follows the
        /// mouse (x PanSpeed), so the point under the mouse stays under it at the current zoom. The offset is kept within PanLimit (view heights).
        /// Expected outcome: returns whether the pan changed.
        internal bool Pan(float dx, float dy, float frameHeightPx)
        {
            if (float.IsNaN(dx) || float.IsNaN(dy)) { Log("ViewerOrbit: pan with NaN ignored"); return false; }
            if (!(frameHeightPx >= 1f) || float.IsInfinity(frameHeightPx)) { Log("ViewerOrbit: pan ignored, frame height " + F(frameHeightPx) + " px is not usable"); return false; }
            float k = _settings.PanSpeed / (frameHeightPx * Math.Max(0.01f, Zoom));       // view heights (at zoom 1) per dragged pixel
            float rx, ry, rz, ux, uy, uz;
            ViewerMath.CameraAxes(Yaw, Pitch, out rx, out ry, out rz, out ux, out uy, out uz);
            // the model moves with the mouse, so the point the camera looks at moves the other way: left / up on screen -> opposite of right / up
            float x = PanX + (-rx * dx + ux * dy) * k;
            float y = PanY + (-ry * dx + uy * dy) * k;
            float z = PanZ + (-rz * dx + uz * dy) * k;
            bool limited = ViewerMath.ClampLength(ref x, ref y, ref z, _settings.PanLimit);
            bool changed = Math.Abs(x - PanX) > Tiny || Math.Abs(y - PanY) > Tiny || Math.Abs(z - PanZ) > Tiny;   // rounding dust is not a change
            if (changed) { PanX = x; PanY = y; PanZ = z; }
            else if (limited) Log("ViewerOrbit: pan at the limit (" + F(_settings.PanLimit) + " view heights), view " + this);
            return changed;
        }

        /// A drag of dx / dy UI pixels (dx > 0 = right, dy > 0 = down): right turns the model to the right, down shows it more from above.
        internal bool Drag(float dx, float dy)
        {
            if (float.IsNaN(dx) || float.IsNaN(dy)) { Log("ViewerOrbit: drag with NaN ignored"); return false; }
            float yaw = ViewerMath.WrapYaw(Yaw - dx * _settings.RotateSpeed);
            float pitch = ViewerMath.ClampPitch(Pitch + dy * _settings.RotateSpeed, _settings.PitchLimit);
            bool changed = yaw != Yaw || pitch != Pitch;
            Yaw = yaw; Pitch = pitch;
            return changed;
        }

        /// Zoom by whole steps (+1 = one step closer, -1 = one step away).
        internal bool StepZoom(float steps)
        {
            float z = ViewerMath.StepZoom(Zoom, steps, _settings.ZoomStep, _settings.ZoomMin, _settings.ZoomMax);
            bool changed = z != Zoom;
            Log("ViewerOrbit: zoom " + F(Zoom) + " -> " + F(z) + " (" + F(steps) + " step(s), range " + F(_settings.ZoomMin) + ".." + F(_settings.ZoomMax) + ")" + (changed ? "" : " - at the limit"));
            Zoom = z;
            return changed;
        }

        /// A mouse wheel turn: UI Toolkit's delta.y is positive when the wheel turns toward the player (= zoom out); one notch = one step.
        internal bool Wheel(float deltaY)
        {
            if (deltaY == 0f || float.IsNaN(deltaY)) return false;
            return StepZoom(deltaY > 0f ? -1f : 1f);
        }

        static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        public override string ToString() { return "yaw " + F(Yaw) + " deg, pitch " + F(Pitch) + " deg, zoom x" + F(Zoom) + ", pan (" + F(PanX) + ", " + F(PanY) + ", " + F(PanZ) + ") view heights"; }

        void Log(string s) { if (_log != null) _log(s); }
    }
}
