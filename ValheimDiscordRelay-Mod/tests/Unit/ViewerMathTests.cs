using System;
using System.Collections.Generic;
using ValheimDiscordRelay.Ui;

namespace ValheimDiscordRelay.Tests.Unit;

/// <summary>The 3D viewer's pure math, orbit state and settings ranges (from WhipItUp, shared through the mod template).</summary>
[TestFixture]
public class ViewerMathTests
{
    const float Eps = 1e-3f;

    [Test]
    public void FitDistance_TallViewport_IsLimitedByWidth()
    {
        // 1 m half width in a square 30 degree view: d = 1 / tan(15) = 3.732
        float d = ViewerMath.FitDistance(1f, 0.1f, 1f, 30f, 1f, 0f);
        Assert.That(d, Is.EqualTo(1f / (float)Math.Tan(15 * Math.PI / 180)).Within(Eps));
    }

    [Test]
    public void FitDistance_WideViewport_NeedsLessDistanceForTheWidth()
    {
        float square = ViewerMath.FitDistance(2f, 0.05f, 0.1f, 30f, 1f, 0f);
        float wide = ViewerMath.FitDistance(2f, 0.05f, 0.1f, 30f, 2.5f, 0f);
        Assert.That(wide, Is.LessThan(square));
        Assert.That(wide, Is.EqualTo(2f / (2.5f * (float)Math.Tan(15 * Math.PI / 180))).Within(Eps));
    }

    [Test]
    public void FitDistance_NeverCloserThanTheMinimumRadii()
    {
        float d = ViewerMath.FitDistance(0.01f, 0.01f, 1f, 90f, 4f, 0f);
        Assert.That(d, Is.EqualTo(ViewerMath.MinDistanceRadii).Within(Eps));
    }

    [Test]
    public void FitDistance_MarginGrowsTheDistance()
    {
        float plain = ViewerMath.FitDistance(1f, 1f, 0.1f, 30f, 1f, 0f);
        float room = ViewerMath.FitDistance(1f, 1f, 0.1f, 30f, 1f, 0.1f);
        Assert.That(room, Is.EqualTo(plain * 1.1f).Within(Eps));
    }

    [Test]
    public void FitDistance_BadInput_GivesAPositiveDistance()
    {
        Assert.That(ViewerMath.FitDistance(float.NaN, -1f, 0f, 0f, float.NaN, float.NaN), Is.GreaterThan(0f));
        Assert.That(ViewerMath.FitDistance(1f, 1f, 1f, 30f, 0f, 0f), Is.GreaterThan(0f));
    }

    [TestCase(10f, 80f, 10f)]
    [TestCase(95f, 80f, 80f)]
    [TestCase(-95f, 80f, -80f)]
    [TestCase(50f, 120f, 50f)]
    [TestCase(95f, 120f, 89f)]
    public void ClampPitch_StaysWithinTheLimit(float pitch, float limit, float expected)
    {
        Assert.That(ViewerMath.ClampPitch(pitch, limit), Is.EqualTo(expected).Within(Eps));
    }

    [TestCase(190f, -170f)]
    [TestCase(-190f, 170f)]
    [TestCase(720f, 0f)]
    [TestCase(180f, 180f)]
    [TestCase(45f, 45f)]
    public void WrapYaw_IsWithinMinus180To180(float yaw, float expected)
    {
        Assert.That(ViewerMath.WrapYaw(yaw), Is.EqualTo(expected).Within(Eps));
    }

    [Test]
    public void StepZoom_MultipliesAndClamps()
    {
        Assert.That(ViewerMath.StepZoom(1f, 1f, 1.2f, 0.5f, 8f), Is.EqualTo(1.2f).Within(Eps));
        Assert.That(ViewerMath.StepZoom(1f, -1f, 1.2f, 0.5f, 8f), Is.EqualTo(1f / 1.2f).Within(Eps));
        Assert.That(ViewerMath.StepZoom(7f, 3f, 1.2f, 0.5f, 8f), Is.EqualTo(8f).Within(Eps));
        Assert.That(ViewerMath.StepZoom(0.6f, -5f, 1.2f, 0.5f, 8f), Is.EqualTo(0.5f).Within(Eps));
    }

    [Test]
    public void Orbit_YawZeroPitchZero_IsInFrontOnMinusZ()
    {
        ViewerMath.Orbit(0f, 0f, 2f, out float x, out float y, out float z);
        Assert.That(x, Is.EqualTo(0f).Within(Eps));
        Assert.That(y, Is.EqualTo(0f).Within(Eps));
        Assert.That(z, Is.EqualTo(-2f).Within(Eps));
    }

    [Test]
    public void Orbit_KeepsTheDistance_AndPitchUpIsAbove()
    {
        ViewerMath.Orbit(37f, 25f, 3f, out float x, out float y, out float z);
        Assert.That(Math.Sqrt(x * x + y * y + z * z), Is.EqualTo(3.0).Within(Eps));
        Assert.That(y, Is.GreaterThan(0f));
    }

    [Test]
    public void Orbit_Reset_IsTheStartView()
    {
        var s = new ViewerSettings { StartYaw = 20f, StartPitch = 12f };
        var o = new ViewerOrbit(s);
        o.Drag(100f, 50f);
        o.StepZoom(2f);
        Assert.That(o.Reset(), Is.True);
        Assert.That(o.Yaw, Is.EqualTo(20f).Within(Eps));
        Assert.That(o.Pitch, Is.EqualTo(12f).Within(Eps));
        Assert.That(o.Zoom, Is.EqualTo(1f).Within(Eps));
        Assert.That(o.Reset(), Is.False, "a second reset changes nothing");
    }

    [Test]
    public void Orbit_DragRight_TurnsYaw_DragDown_RaisesPitch_WithinTheLimit()
    {
        var s = new ViewerSettings { StartYaw = 0f, StartPitch = 0f, RotateSpeed = 0.5f, PitchLimit = 30f };
        var o = new ViewerOrbit(s);
        Assert.That(o.Drag(20f, 0f), Is.True);
        Assert.That(o.Yaw, Is.EqualTo(-10f).Within(Eps));
        o.Drag(0f, 1000f);
        Assert.That(o.Pitch, Is.EqualTo(30f).Within(Eps));
        Assert.That(o.Drag(0f, 10f), Is.False, "at the pitch limit a further drag down changes nothing");
    }

    [Test]
    public void Orbit_Wheel_TowardThePlayer_ZoomsOut()
    {
        var log = new List<string>();
        var o = new ViewerOrbit(new ViewerSettings(), log.Add);
        o.Wheel(1f);
        Assert.That(o.Zoom, Is.LessThan(1f));
        o.Wheel(-1f);
        o.Wheel(-1f);
        Assert.That(o.Zoom, Is.GreaterThan(1f));
        Assert.That(o.Wheel(0f), Is.False);
        Assert.That(log.Exists(l => l.Contains("ViewerOrbit: zoom")), Is.True, "zoom changes are logged");
    }

    [Test]
    public void Settings_Clamp_PutsEveryValueIntoItsRange()
    {
        var s = new ViewerSettings { RotateSpeed = 99f, ZoomStep = 0f, ZoomMax = float.NaN, FrameHeight = 5f, PitchLimit = 10f, StartPitch = 45f };
        var log = new List<string>();
        int changed = s.Clamp(log.Add);
        Assert.That(changed, Is.EqualTo(5));
        Assert.That(s.RotateSpeed, Is.EqualTo(ViewerSettings.RotateSpeedMax));
        Assert.That(s.ZoomStep, Is.EqualTo(ViewerSettings.ZoomStepMin));
        Assert.That(s.ZoomMax, Is.EqualTo(ViewerSettings.ZoomMaxDefault));
        Assert.That(s.FrameHeight, Is.EqualTo(ViewerSettings.FrameHeightMin));
        Assert.That(s.StartPitch, Is.EqualTo(10f));
        Assert.That(log.Count, Is.EqualTo(6), "one line per fixed value + the summary");
    }

    [Test]
    public void Settings_Clamp_PutsTheLightsIntoTheirRanges()
    {
        var s = new ViewerSettings { KeyLight = 5f, FillLight = -1f, Ambient = float.NaN };
        int changed = s.Clamp();
        Assert.That(changed, Is.EqualTo(3));
        Assert.That(s.KeyLight, Is.EqualTo(ViewerSettings.KeyLightMax));
        Assert.That(s.FillLight, Is.EqualTo(ViewerSettings.FillLightMin));
        Assert.That(s.Ambient, Is.EqualTo(ViewerSettings.AmbientDefault));
    }

    [Test]
    public void DefaultLight_KeepsAPaleColourBelowWhite()
    {
        // user 2026-10-28: the pale-blue costume (brightest channel ~0.85 with the fur tips' x1.3) showed white: key + fill + ambient
        // facing the camera must stay below 1 for such a colour (the view has no tone mapping, every channel above 1 clips)
        float lit = ViewerSettings.KeyLightDefault + ViewerSettings.FillLightDefault + ViewerSettings.AmbientDefault;
        Assert.That(0.85f * lit, Is.LessThan(1f), "light sum " + lit);
        Assert.That(lit, Is.GreaterThan(0.8f), "and not too dark either");
    }

    [Test]
    public void CameraAxes_FrontView_RightIsX_UpIsY()
    {
        ViewerMath.CameraAxes(0f, 0f, out float rx, out float ry, out float rz, out float ux, out float uy, out float uz);
        Assert.That(new[] { rx, ry, rz, ux, uy, uz }, Is.EqualTo(new[] { 1f, 0f, 0f, 0f, 1f, 0f }).Within(Eps));
    }

    [Test]
    public void CameraAxes_AreUnitLength_AndSquareToEachOtherAndTheViewDirection()
    {
        foreach (float yaw in new[] { -170f, -45f, 0f, 20f, 90f, 135f })
        {
            foreach (float pitch in new[] { -80f, -12f, 0f, 30f, 80f })
            {
                ViewerMath.CameraAxes(yaw, pitch, out float rx, out float ry, out float rz, out float ux, out float uy, out float uz);
                ViewerMath.Orbit(yaw, pitch, 1f, out float cx, out float cy, out float cz);   // camera position = minus the view direction
                Assert.That(Math.Sqrt(rx * rx + ry * ry + rz * rz), Is.EqualTo(1.0).Within(Eps));
                Assert.That(Math.Sqrt(ux * ux + uy * uy + uz * uz), Is.EqualTo(1.0).Within(Eps));
                Assert.That(rx * ux + ry * uy + rz * uz, Is.EqualTo(0f).Within(Eps), "right and up are square, yaw " + yaw + " pitch " + pitch);
                Assert.That(rx * cx + ry * cy + rz * cz, Is.EqualTo(0f).Within(Eps), "right is square to the view direction");
                Assert.That(ux * cx + uy * cy + uz * cz, Is.EqualTo(0f).Within(Eps), "up is square to the view direction");
            }
        }
    }

    [Test]
    public void ClampLength_KeepsTheDirection_AndReportsShortening()
    {
        float x = 3f, y = 4f, z = 0f;
        Assert.That(ViewerMath.ClampLength(ref x, ref y, ref z, 1f), Is.True);
        Assert.That(x, Is.EqualTo(0.6f).Within(Eps));
        Assert.That(y, Is.EqualTo(0.8f).Within(Eps));
        float a = 0.1f, b = 0.1f, c = 0f;
        Assert.That(ViewerMath.ClampLength(ref a, ref b, ref c, 1f), Is.False, "inside the limit nothing changes");
        float n = float.NaN, m = 1f, o = 1f;
        Assert.That(ViewerMath.ClampLength(ref n, ref m, ref o, 1f), Is.True);
        Assert.That(new[] { n, m, o }, Is.EqualTo(new[] { 0f, 0f, 0f }));
    }

    [Test]
    public void Orbit_Pan_RightDragMovesTheLookAtPointLeft_DragDownMovesItUp()
    {
        var s = new ViewerSettings { StartYaw = 0f, StartPitch = 0f, PanSpeed = 1f, PanLimit = 3f };
        var o = new ViewerOrbit(s);
        Assert.That(o.Pan(100f, 0f, 400f), Is.True);
        Assert.That(o.PanX, Is.EqualTo(-0.25f).Within(Eps), "100 px of a 400 px frame = a quarter of the view height, model follows the mouse right");
        Assert.That(o.PanY, Is.EqualTo(0f).Within(Eps));
        Assert.That(o.PanZ, Is.EqualTo(0f).Within(Eps));
        o.Pan(0f, 40f, 400f);
        Assert.That(o.PanY, Is.EqualTo(0.1f).Within(Eps), "the model moved down, so the look-at point went up");
    }

    [Test]
    public void Orbit_Pan_ZoomedIn_MovesLessPerPixel_PanSpeedScalesIt()
    {
        var s = new ViewerSettings { StartYaw = 0f, StartPitch = 0f, PanSpeed = 2f, PanLimit = 3f };
        var o = new ViewerOrbit(s);
        o.StepZoom(0f);
        o.Pan(100f, 0f, 400f);
        Assert.That(o.PanX, Is.EqualTo(-0.5f).Within(Eps), "speed x2");
        var z = new ViewerOrbit(new ViewerSettings { StartYaw = 0f, StartPitch = 0f, PanSpeed = 1f, PanLimit = 3f });
        while (z.Zoom < 2f) z.StepZoom(1f);
        float zoom = z.Zoom;
        z.Pan(100f, 0f, 400f);
        Assert.That(z.PanX, Is.EqualTo(-0.25f / zoom).Within(Eps), "zoomed in x" + zoom + " the same drag moves the world point less");
    }

    [Test]
    public void Orbit_Pan_FollowsTheTurnedView_AndStaysWithinTheLimit()
    {
        var s = new ViewerSettings { StartYaw = 90f, StartPitch = 0f, PanSpeed = 1f, PanLimit = 0.5f };
        var o = new ViewerOrbit(s);
        o.Pan(100f, 0f, 400f);                                      // camera on -X looking +X: its right is -Z, so the look-at point moves +Z
        Assert.That(o.PanX, Is.EqualTo(0f).Within(Eps));
        Assert.That(o.PanZ, Is.EqualTo(0.25f).Within(Eps));
        o.Pan(1000f, 0f, 400f);
        Assert.That(o.PanZ, Is.EqualTo(0.5f).Within(Eps), "limited to PanLimit");
        var log = new List<string>();
        var l = new ViewerOrbit(s, log.Add);
        l.Pan(1000f, 0f, 400f);
        Assert.That(l.Pan(10f, 0f, 400f), Is.False, "at the limit a further drag in the same direction changes nothing");
        Assert.That(log.Exists(t => t.Contains("pan at the limit")), Is.True);
    }

    [Test]
    public void Orbit_Pan_BadInput_IsIgnored_ResetClearsThePan()
    {
        var log = new List<string>();
        var o = new ViewerOrbit(new ViewerSettings(), log.Add);
        Assert.That(o.Pan(float.NaN, 1f, 400f), Is.False);
        Assert.That(o.Pan(10f, 10f, 0f), Is.False, "no frame height, nothing to measure against");
        Assert.That(log.Exists(t => t.Contains("frame height")), Is.True);
        o.Pan(50f, 30f, 400f);
        Assert.That(o.PanX != 0f || o.PanY != 0f || o.PanZ != 0f, Is.True);
        Assert.That(o.Reset(), Is.True, "a panned view is not the default view");
        Assert.That(new[] { o.PanX, o.PanY, o.PanZ }, Is.EqualTo(new[] { 0f, 0f, 0f }));
    }

    [Test]
    public void Settings_Clamp_PanValuesAreRanged()
    {
        var s = new ViewerSettings { PanSpeed = 99f, PanLimit = -1f };
        Assert.That(s.Clamp(), Is.EqualTo(2));
        Assert.That(s.PanSpeed, Is.EqualTo(ViewerSettings.PanSpeedMax));
        Assert.That(s.PanLimit, Is.EqualTo(ViewerSettings.PanLimitMin));
    }

    [Test]
    public void Settings_Defaults_AreInsideTheirRanges()
    {
        var s = new ViewerSettings();
        Assert.That(s.Clamp(), Is.EqualTo(0));
    }
}
