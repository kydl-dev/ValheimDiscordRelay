using System.Collections;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Which screenshot was asked for (key or F6 hub button) and the hiding of the HUD / player around it.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        /// <summary>Frames to wait after hiding the HUD so the game has drawn a frame without it before the capture starts.</summary>
        private const int HiddenSettleFrames = 2;

        private readonly ScreenshotSceneHider _sceneHider = new ScreenshotSceneHider();

        /// <summary>What the screenshot in progress shows; Normal when none is running.</summary>
        private ScreenshotMode _screenshotMode = ScreenshotMode.Normal;

        /// <summary>True while a screenshot is waiting, being captured or uploading its capture (read by the TestLab hooks).</summary>
        internal bool ScreenshotRunning { get { return _screenshotInProgress; } }

        /// <summary>The suffix of the uploaded file name that tells which kind of screenshot it is.</summary>
        private static string ModeFileSuffix(ScreenshotMode mode)
        {
            if (mode == ScreenshotMode.HudHidden)
                return "-nohud";

            return mode == ScreenshotMode.HudAndPlayerHidden ? "-nohud-noplayer" : string.Empty;
        }

        /// <summary>
        /// Called every frame: checks the three screenshot keys. The normal screenshot (HUD on) works at any time; the two
        /// HUD-free ones are ignored while the player types in the chat, the console or a text box (P is a letter).
        /// </summary>
        private void TickScreenshotHotkeys()
        {
            // Safety net: if a screenshot coroutine was killed half way, the HUD must not stay hidden.
            if (!_screenshotInProgress)
                _sceneHider.Restore();

            if (!ClientConfig.ScreenshotsActive || _screenshotInProgress)
                return;

            KeyboardShortcut normal = ClientConfig.ScreenshotKey.Value;
            KeyboardShortcut hud = ClientConfig.ScreenshotHudHiddenKey.Value;
            KeyboardShortcut bare = ClientConfig.ScreenshotHudPlayerHiddenKey.Value;

            // Most specific first: Alt+P must not also count as P.
            ScreenshotMode mode;
            if (IsScreenshotHotkeyDown(bare, hud, normal))
                mode = ScreenshotMode.HudAndPlayerHidden;
            else if (IsScreenshotHotkeyDown(hud, bare, normal))
                mode = ScreenshotMode.HudHidden;
            else if (IsScreenshotHotkeyDown(normal, bare, hud))
                mode = ScreenshotMode.Normal;
            else
                return;

            if (mode != ScreenshotMode.Normal && ClientTextUtils.IsTypingInGame())
                return;

            RequestScreenshot(mode, 0f);
        }

        /// <summary>
        /// True on the frame the shortcut's key goes down. Deliberately not <c>KeyboardShortcut.IsDown()</c>: BepInEx's version
        /// also rejects the press while any other key (W, Shift) is held, which made the screenshot silently fail during gameplay.
        /// Here only the main key and the configured modifiers matter. A key that another shortcut shares (P and Alt+P) only
        /// counts when none of the OTHER shortcut's extra modifiers is held. Left and right Alt / Ctrl / Shift count the same.
        /// </summary>
        private static bool IsScreenshotHotkeyDown(KeyboardShortcut shortcut, params KeyboardShortcut[] siblings)
        {
            KeyCode mainKey = shortcut.MainKey;
            if (mainKey == KeyCode.None || !Input.GetKeyDown(mainKey))
                return false;

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!IsHeld(modifier))
                    return false;
            }

            foreach (KeyboardShortcut other in siblings)
            {
                if (other.MainKey != mainKey)
                    continue;

                foreach (KeyCode modifier in other.Modifiers)
                {
                    if (!System.Linq.Enumerable.Contains(shortcut.Modifiers, modifier) && IsHeld(modifier))
                        return false;
                }
            }

            return true;
        }

        private static bool IsHeld(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                default:
                    return Input.GetKey(key);
            }
        }

        /// <summary>
        /// Starts one screenshot of the given kind (from a key or from the F6 hub). Does nothing while another one is running.
        /// </summary>
        /// <param name="mode">What the screenshot shows.</param>
        /// <param name="delaySeconds">How long to wait first, e.g. for the F6 window to disappear.</param>
        internal void RequestScreenshot(ScreenshotMode mode, float delaySeconds)
        {
            if (_screenshotInProgress)
            {
                RelayDiagnostics.Debug("[Screenshot] A screenshot is already running; the " + mode + " request is ignored.");
                return;
            }

            _host.StartCoroutine(CaptureRequestedScreenshot(mode, delaySeconds));
        }

        private IEnumerator CaptureRequestedScreenshot(ScreenshotMode mode, float delaySeconds)
        {
            // Set at once, so no second request starts during the delay or while the HUD is hidden.
            _screenshotInProgress = true;

            try
            {
                if (delaySeconds > 0f)
                {
                    float until = Time.unscaledTime + delaySeconds;
                    while (Time.unscaledTime < until)
                        yield return null;
                }

                if (!_sceneHider.Apply(mode))
                {
                    RelayDiagnostics.Warning("Screenshot (" + mode + ") skipped: the HUD could not be hidden in this game version.");
                    yield break;
                }

                if (mode != ScreenshotMode.Normal)
                {
                    for (int i = 0; i < HiddenSettleFrames; i++)
                        yield return null;
                }

                _screenshotMode = mode;
                RelayDiagnostics.Info("Screenshot requested: " + mode + ".");

                // Run the capture inside this coroutine (not as a nested one) so the HUD is restored in the same step the capture ends.
                IEnumerator capture = CaptureManualScreenshot();
                while (capture.MoveNext())
                    yield return capture.Current;
            }
            finally
            {
                _screenshotMode = ScreenshotMode.Normal;
                _sceneHider.Restore();
                _screenshotInProgress = false;
            }
        }
    }
}
