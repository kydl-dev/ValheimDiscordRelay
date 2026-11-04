using System;
using System.Globalization;

namespace ValheimDiscordRelay.Ui;

/// <summary>
/// The adjustable values of the 3D model viewer (from WhipItUp, shared through the mod template). Plain data with a min / max per value
/// (PROJECT_RULES 11): the game may build it from Tunables with the same ranges, the Unity preview uses the defaults. No Unity / Valheim types (unit-tested).
/// </summary>
internal sealed class ViewerSettings
{
    // Ranges: the Tunables in ModSettings use these same numbers.
    internal const float RotateSpeedMin = 0.05f, RotateSpeedMax = 2f, RotateSpeedDefault = 0.4f;     // degrees per UI pixel dragged
    internal const float ZoomStepMin = 1.02f, ZoomStepMax = 2f, ZoomStepDefault = 1.2f;              // magnification factor per wheel notch / button press
    internal const float ZoomMaxMin = 1f, ZoomMaxMax = 20f, ZoomMaxDefault = 8f;                     // most magnification (1 = the whole model fits)
    internal const float ZoomMinMin = 0.2f, ZoomMinMax = 1f, ZoomMinDefault = 0.5f;                  // least magnification (below 1 = smaller than the frame)
    internal const float PitchLimitMin = 0f, PitchLimitMax = 89f, PitchLimitDefault = 80f;           // degrees up / down the camera may tilt
    internal const float FrameHeightMin = 200f, FrameHeightMax = 800f, FrameHeightDefault = 420f;    // viewport height in UI pixels (1080 p reference)
    internal const float MarginMin = 0f, MarginMax = 0.5f, MarginDefault = 0.08f;                    // room around the model at zoom 1 (0.08 = 8 %)
    internal const float StartYawMin = -180f, StartYawMax = 180f, StartYawDefault = 20f;             // degrees the default view is turned
    internal const float StartPitchMin = -89f, StartPitchMax = 89f, StartPitchDefault = 12f;         // degrees the default view looks down
    internal const float PanSpeedMin = 0.2f, PanSpeedMax = 3f, PanSpeedDefault = 1f;                 // right-drag: 1 = the model follows the mouse exactly
    internal const float PanLimitMin = 0f, PanLimitMax = 3f, PanLimitDefault = 0.8f;                 // how far the model may be moved, in view heights at zoom 1 (0.8 = about half a model length)
    // Studio light of the view (user 2026-10-28: the pale-blue costume showed white - 1.1 + 0.45 + 0.37 ambient clipped every channel; no tone mapping here)
    internal const float KeyLightMin = 0f, KeyLightMax = 2f, KeyLightDefault = 0.7f;                 // main light, from up-left behind the camera
    internal const float FillLightMin = 0f, FillLightMax = 1.5f, FillLightDefault = 0.25f;           // softer light from the right
    internal const float AmbientMin = 0f, AmbientMax = 1f, AmbientDefault = 0.22f;                   // grey level of the light from everywhere

    internal float RotateSpeed = RotateSpeedDefault;
    internal float ZoomStep = ZoomStepDefault;
    internal float ZoomMax = ZoomMaxDefault;
    internal float ZoomMin = ZoomMinDefault;
    internal float PitchLimit = PitchLimitDefault;
    internal float FrameHeight = FrameHeightDefault;
    internal float Margin = MarginDefault;
    internal float StartYaw = StartYawDefault;
    internal float StartPitch = StartPitchDefault;
    internal float PanSpeed = PanSpeedDefault;
    internal float PanLimit = PanLimitDefault;
    internal float KeyLight = KeyLightDefault;
    internal float FillLight = FillLightDefault;
    internal float Ambient = AmbientDefault;

    /// Puts every value into its range (NaN = the default). Expected outcome: returns how many values were changed.
    internal int Clamp(Action<string> log = null)
    {
        int changed = 0;
        RotateSpeed = Fix("RotateSpeed", RotateSpeed, RotateSpeedMin, RotateSpeedMax, RotateSpeedDefault, ref changed, log);
        ZoomStep = Fix("ZoomStep", ZoomStep, ZoomStepMin, ZoomStepMax, ZoomStepDefault, ref changed, log);
        ZoomMax = Fix("ZoomMax", ZoomMax, ZoomMaxMin, ZoomMaxMax, ZoomMaxDefault, ref changed, log);
        ZoomMin = Fix("ZoomMin", ZoomMin, ZoomMinMin, ZoomMinMax, ZoomMinDefault, ref changed, log);
        PitchLimit = Fix("PitchLimit", PitchLimit, PitchLimitMin, PitchLimitMax, PitchLimitDefault, ref changed, log);
        FrameHeight = Fix("FrameHeight", FrameHeight, FrameHeightMin, FrameHeightMax, FrameHeightDefault, ref changed, log);
        Margin = Fix("Margin", Margin, MarginMin, MarginMax, MarginDefault, ref changed, log);
        StartYaw = Fix("StartYaw", StartYaw, StartYawMin, StartYawMax, StartYawDefault, ref changed, log);
        StartPitch = Fix("StartPitch", StartPitch, StartPitchMin, StartPitchMax, StartPitchDefault, ref changed, log);
        PanSpeed = Fix("PanSpeed", PanSpeed, PanSpeedMin, PanSpeedMax, PanSpeedDefault, ref changed, log);
        PanLimit = Fix("PanLimit", PanLimit, PanLimitMin, PanLimitMax, PanLimitDefault, ref changed, log);
        KeyLight = Fix("KeyLight", KeyLight, KeyLightMin, KeyLightMax, KeyLightDefault, ref changed, log);
        FillLight = Fix("FillLight", FillLight, FillLightMin, FillLightMax, FillLightDefault, ref changed, log);
        Ambient = Fix("Ambient", Ambient, AmbientMin, AmbientMax, AmbientDefault, ref changed, log);
        if (StartPitch > PitchLimit || StartPitch < -PitchLimit)
        {
            float was = StartPitch;
            StartPitch = Math.Max(-PitchLimit, Math.Min(PitchLimit, StartPitch));
            changed++;
            if (log != null) log("ViewerSettings: StartPitch " + F(was) + " is outside PitchLimit " + F(PitchLimit) + ", using " + F(StartPitch));
        }
        if (log != null) log("ViewerSettings: " + this + " (" + changed + " value(s) put into range)");
        return changed;
    }

    static float Fix(string name, float v, float min, float max, float def, ref int changed, Action<string> log)
    {
        float r = float.IsNaN(v) || float.IsInfinity(v) ? def : Math.Max(min, Math.Min(max, v));
        if (!(Math.Abs(r - v) <= 1e-6f))   // changed (a NaN or infinite value always counts as changed)
        {
            changed++;
            if (log != null) log("ViewerSettings: " + name + " " + F(v) + " is outside " + F(min) + ".." + F(max) + ", using " + F(r));
        }
        return r;
    }

    static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    public override string ToString()
    {
        return "rotate " + F(RotateSpeed) + " deg/px, zoom step x" + F(ZoomStep) + ", zoom " + F(ZoomMin) + ".." + F(ZoomMax) + ", pitch limit " + F(PitchLimit)
            + " deg, frame " + F(FrameHeight) + " px, margin " + F(Margin) + ", start yaw " + F(StartYaw) + " / pitch " + F(StartPitch) + " deg, pan speed x" + F(PanSpeed)
            + ", pan limit " + F(PanLimit) + " view heights, light key " + F(KeyLight) + " / fill " + F(FillLight) + " / ambient " + F(Ambient);
    }
}
