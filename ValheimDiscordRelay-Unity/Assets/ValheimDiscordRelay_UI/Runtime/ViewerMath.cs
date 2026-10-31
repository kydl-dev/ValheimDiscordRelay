using System;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// Pure float math of the 3D viewer: how far the camera stands so the model fits, zoom / pitch limits, where the camera is for a yaw / pitch.
    /// No Unity / Valheim types (unit-tested: ViewerMathTests). Angles in degrees, lengths in metres.
    /// </summary>
    internal static class ViewerMath
    {
        const double Rad = Math.PI / 180.0;

        /// The camera never comes closer to the model's centre than this many bounding-sphere radii (so turning a long model toward the camera
        /// never puts its tip behind the camera).
        internal const float MinDistanceRadii = 1.5f;

        /// Camera distance from the model's centre at zoom 1 for a box of half width `halfWidth` (across the view), half height `halfHeight`
        /// (up the view) and bounding-sphere `radius`, with a vertical field of view and the viewport's aspect (width / height); `margin` = extra
        /// room (0.08 = 8 %). Expected outcome: a distance > 0 (bad input: the result for a 0.5 m model in a square 30 degree view).
        internal static float FitDistance(float halfWidth, float halfHeight, float radius, float verticalFovDeg, float aspect, float margin)
        {
            if (!(verticalFovDeg > 1f && verticalFovDeg < 179f)) verticalFovDeg = 30f;
            if (!(aspect > 0.01f) || float.IsInfinity(aspect)) aspect = 1f;
            if (!(margin >= 0f) || float.IsInfinity(margin)) margin = 0f;
            if (!(radius > 0f) || float.IsInfinity(radius)) radius = 0.5f;
            halfWidth = halfWidth > 0f && !float.IsInfinity(halfWidth) ? halfWidth : radius;
            halfHeight = halfHeight > 0f && !float.IsInfinity(halfHeight) ? halfHeight : radius;
            double tanV = Math.Tan(verticalFovDeg * 0.5 * Rad);
            double tanH = tanV * aspect;
            double grow = 1.0 + margin;
            double byWidth = halfWidth * grow / tanH;
            double byHeight = halfHeight * grow / tanV;
            double least = radius * MinDistanceRadii;
            return (float)Math.Max(least, Math.Max(byWidth, byHeight));
        }

        /// Pitch kept within -limit..limit (limit itself 0..89).
        internal static float ClampPitch(float pitch, float limit)
        {
            limit = Math.Max(0f, Math.Min(89f, float.IsNaN(limit) ? 0f : limit));
            if (float.IsNaN(pitch)) return 0f;
            return Math.Max(-limit, Math.Min(limit, pitch));
        }

        /// Yaw wrapped into -180..180 (a long drag never grows the number).
        internal static float WrapYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0f;
            double y = yaw % 360.0;
            if (y > 180.0) y -= 360.0;
            if (y <= -180.0) y += 360.0;
            return (float)y;
        }

        /// Magnification after `steps` zoom steps (positive = closer) of `factor` each, kept within min..max (min <= max is enforced).
        internal static float StepZoom(float zoom, float steps, float factor, float min, float max)
        {
            if (!(min > 0f)) min = 0.01f;
            if (max < min) max = min;
            if (!(factor > 1f)) factor = 1f;
            if (!(zoom > 0f) || float.IsInfinity(zoom)) zoom = 1f;
            double z = zoom * Math.Pow(factor, float.IsNaN(steps) ? 0f : steps);
            return (float)Math.Max(min, Math.Min(max, z));
        }

        /// Camera position relative to the model's centre for a yaw (turn around the up axis) and pitch (positive = from above) at `distance`.
        /// Yaw 0, pitch 0 = the camera on the -Z side looking toward +Z (the model's front faces the camera).
        internal static void Orbit(float yawDeg, float pitchDeg, float distance, out float x, out float y, out float z)
        {
            double yaw = yawDeg * Rad, pitch = pitchDeg * Rad;
            double flat = distance * Math.Cos(pitch);
            x = (float)(-flat * Math.Sin(yaw));
            y = (float)(distance * Math.Sin(pitch));
            z = (float)(-flat * Math.Cos(yaw));
        }

        /// The camera's right and up axes (unit vectors in world space) for a yaw / pitch, as Orbit places the camera: the camera looks at the
        /// model, so right stays level and up tilts with the pitch. Yaw 0, pitch 0 = right (1, 0, 0), up (0, 1, 0).
        internal static void CameraAxes(float yawDeg, float pitchDeg, out float rx, out float ry, out float rz, out float ux, out float uy, out float uz)
        {
            double yaw = yawDeg * Rad, pitch = pitchDeg * Rad;
            double sy = Math.Sin(yaw), cy = Math.Cos(yaw), sp = Math.Sin(pitch), cp = Math.Cos(pitch);
            rx = (float)cy; ry = 0f; rz = (float)-sy;
            ux = (float)(sp * sy); uy = (float)cp; uz = (float)(sp * cy);
        }

        /// A vector scaled down so its length is at most `limit` (the direction is kept). Returns whether it had to be shortened.
        internal static bool ClampLength(ref float x, ref float y, ref float z, float limit)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { x = 0f; y = 0f; z = 0f; return true; }
            limit = float.IsNaN(limit) ? 0f : Math.Max(0f, limit);
            double len = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
            if (len <= limit) return false;
            double k = len > 0.0 ? limit / len : 0.0;
            x = (float)(x * k); y = (float)(y * k); z = (float)(z * k);
            return true;
        }
    }
}
