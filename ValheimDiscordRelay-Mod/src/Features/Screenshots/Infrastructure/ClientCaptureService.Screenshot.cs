using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// The manual hotkey screenshot path, with a built-in black-frame check and automatic retry.
///
/// A screenshot is captured with one strategy after another until one produces a frame that is not black:
/// the strategy this PC used successfully last time first (it is saved between game sessions), then the rest in
/// the default order, and finally the newest frame from the rolling death-video buffer as a last resort.
/// A strategy that rescues a black frame becomes the new first choice, so a machine on which the fast path returns
/// black only pays for the retries once, not at every start.
/// </summary>
internal sealed partial class ClientCaptureService
{
    /// <summary>
    /// How a screenshot frame is obtained. The first four capture the screen; they are tried in this order unless a
    /// saved preference moves one to the front. <see cref="BufferedFrame"/> is always the very last resort.
    /// </summary>
    private enum ScreenshotStrategy
    {
        /// <summary>GPU capture into a render texture, read back in its own RGBA32 layout; alpha dropped on the CPU (no GPU format conversion).</summary>
        Native = 0,

        /// <summary>GPU capture into a render texture, read back asynchronously as RGB24 straight from that texture.</summary>
        Direct = 1,

        /// <summary>Same, but copied through an intermediate render texture first (the path death-video frames take).</summary>
        Blit = 2,

        /// <summary>Unity's synchronous CaptureScreenshotAsTexture. Stalls the game for a moment, but reads the screen itself.</summary>
        Synchronous = 3,

        /// <summary>
        /// Not a capture: re-uses the newest frame of the rolling death-video buffer (at most the death-video resolution,
        /// sent as JPEG). Only available while death-video recording is on. Never saved as the preferred strategy.
        /// </summary>
        BufferedFrame = 4
    }

    /// <summary>The real capture strategies, in their default order (the buffered-frame fallback is appended separately).</summary>
    private static readonly ScreenshotStrategy[] CaptureStrategies =
    {
        ScreenshotStrategy.Native,
        ScreenshotStrategy.Direct,
        ScreenshotStrategy.Blit,
        ScreenshotStrategy.Synchronous
    };

    /// <summary>A readback normally completes within a few frames; give up on one that takes this long.</summary>
    private const float ScreenshotAttemptTimeoutSeconds = 3f;

    /// <summary>The screenshot render texture is kept this long after the last screenshot, then freed.</summary>
    private const float ScreenshotIdleReleaseSeconds = 60f;

    /// <summary>A buffered death-video frame older than this is not used as a screenshot.</summary>
    private const float BufferedFrameMaxAgeSeconds = 3f;

    private const string ScreenshotStrategyFileName = "ValheimDiscordRelay.ScreenshotStrategy.txt";

    /// <summary>
    /// Dedicated to screenshots, so a hotkey press never shares render textures or scratch buffers with the rolling
    /// death-video capture that is running at the same time.
    /// </summary>
    private ScreenCapturePipeline _screenshotPipeline;

    /// <summary>The strategy the next screenshot starts with; loaded from the saved preference and moved forward when a later strategy rescues a black frame.</summary>
    private ScreenshotStrategy _screenshotStrategy = ScreenshotStrategy.Native;

    /// <summary>Time.unscaledTime at which the idle screenshot render texture is freed; 0 when nothing is scheduled.</summary>
    private float _screenshotIdleReleaseAt;

    /// <summary>The outcome of one capture attempt. Written on the main thread only.</summary>
    private sealed class ScreenshotAttempt
    {
        public bool Done;
        public bool Failed;
        public bool Abandoned;
        public bool Black;
        public int Width;
        public int Height;
        public FrameMeasure.FrameStats Stats;

        /// <summary>Why the attempt failed, when known; appended to the log line.</summary>
        public string Note;

        /// <summary>Encoded image bytes; only produced for a usable frame, or for the last capture attempt so there is always something to send.</summary>
        public byte[] Image;

        public string Extension = "png";
        public string ContentType = "image/png";

        /// <summary>Appended to the uploaded file name (before the extension) to show the image is not a normal screenshot.</summary>
        public string FilenameSuffix = string.Empty;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The screenshot itself
    // ---------------------------------------------------------------------------------------------------------

    private IEnumerator CaptureManualScreenshot()
    {
        _screenshotInProgress = true;
        _screenshotIdleReleaseAt = 0f;

        try
        {
            string webhookUrl = ClientConfig.ScreenshotWebhookFor(_screenshotMode);
            if (webhookUrl.Length == 0)
            {
                RelayDiagnostics.Warning("Screenshot (" + _screenshotMode + ") requested, but no webhook is set for it and [Screenshots] Server - Webhook URL is empty.");
                yield break;
            }

            string playerName = ClientConfig.IncludeScreenshotPlayerName.Value
                ? ClientTextUtils.GetLocalPlayerName()
                : "Valheim Player";


            ScreenshotStrategy[] order = BuildScreenshotStrategyOrder();

            // The last attempt is the buffered-frame fallback, which never encodes a black image; so the last real
            // capture is the one that keeps its (black) result in case nothing better turns up.
            int lastCaptureIndex = order.Length - 2;

            // Taken now, at the moment of the key press.
            string context = DescribeScreenshotContext();

            // A freshly created render texture is warmed up with a throwaway capture a frame before the real one.
            bool freshTextures = order[0] != ScreenshotStrategy.Synchronous &&
                                 (_screenshotPipeline == null || !_screenshotPipeline.HasLiveTextures);

            if (freshTextures)
            {
                yield return new WaitForEndOfFrame();

                if (_screenshotPipeline == null)
                    _screenshotPipeline = new ScreenCapturePipeline();

                _screenshotPipeline.WarmUp();
            }

            ScreenshotAttempt accepted = null;
            ScreenshotAttempt blackFallback = null;
            int goodIndex = -1;
            ScreenshotStrategy goodStrategy = order[0];

            for (int i = 0; i < order.Length; i++)
            {
                ScreenshotStrategy strategy = order[i];
                bool isLastAttempt = i == order.Length - 1;
                string attemptLabel = "Screenshot attempt " + (i + 1) + "/" + order.Length + " (" + strategy + ")";

                // Screen capture is only reliable once the frame has finished rendering, so every attempt, including
                // a retry, starts at the end of a frame.
                yield return new WaitForEndOfFrame();

                ScreenshotAttempt attempt = new ScreenshotAttempt();
                StartScreenshotAttempt(strategy, attempt, i == lastCaptureIndex);

                float deadline = Time.unscaledTime + ScreenshotAttemptTimeoutSeconds;
                while (!attempt.Done && Time.unscaledTime < deadline)
                    yield return null;

                if (!attempt.Done)
                {
                    // A late callback must not touch anything.
                    attempt.Abandoned = true;
                    RelayDiagnostics.Warning(attemptLabel + " timed out waiting for the GPU readback" + (isLastAttempt ? "." : "; retrying."));
                    continue;
                }

                if (!attempt.Failed && !attempt.Black && attempt.Image != null)
                {
                    accepted = attempt;
                    goodIndex = i;
                    goodStrategy = strategy;
                    break;
                }

                if (attempt.Image != null)
                    blackFallback = attempt;

                if (attempt.Failed)
                {
                    RelayDiagnostics.Warning(attemptLabel + " failed" + (string.IsNullOrEmpty(attempt.Note) ? string.Empty : ": " + attempt.Note) +
                                             (isLastAttempt ? "." : "; retrying."));
                }
                else
                {
                    RelayDiagnostics.Warning(attemptLabel + " came back black (" + attempt.Stats + ")" + (isLastAttempt ? "." : "; retrying."));
                }
            }

            // One context line per screenshot, whatever the outcome: whether a player existed, whether the game was
            // loading or teleporting, and what the capture texture looked like. This is what tells a screen that was
            // genuinely black (loading screen, menu) apart from a capture that really failed.
            RelayDiagnostics.Info("Screenshot context: " + context + ", freshTextures=" + freshTextures + ", " +
                                  (_screenshotPipeline != null ? _screenshotPipeline.DescribeTextures() : "captureTexture=none") + ".");

            if (accepted != null && goodStrategy == ScreenshotStrategy.BufferedFrame)
            {
                RelayDiagnostics.Warning("Every full-size capture strategy failed or came back black; sending the newest buffered death-video frame instead (" +
                                         accepted.Width + "x" + accepted.Height + ", " + accepted.Stats + ").");
            }
            else if (accepted != null && goodIndex > 0)
            {
                // An earlier strategy gave a black or failed frame and a later one gave a real picture: start with the
                // working one from now on, including in the next game session.
                _screenshotStrategy = goodStrategy;
                SaveScreenshotStrategy(goodStrategy);
                RelayDiagnostics.Info("Screenshot recovered with the " + goodStrategy + " strategy after " + goodIndex +
                                      " black/failed attempt(s); it is saved as this PC's first-choice strategy from now on.");
            }

            if (accepted != null)
            {
                // One line per screenshot, even when nothing went wrong, so a "my screenshot is black" report can be
                // checked against what the capture actually measured (real brightness here means the problem is downstream).
                RelayDiagnostics.Info("Screenshot captured with the " + goodStrategy + " strategy (attempt " + (goodIndex + 1) + "/" +
                                      order.Length + "): " + accepted.Width + "x" + accepted.Height + ", " + accepted.Stats +
                                      ", " + accepted.Extension.ToUpperInvariant() + " " + (accepted.Image.Length / 1024) + " KB.");
            }

            ScreenshotAttempt toSend = accepted;

            if (toSend == null)
            {
                toSend = blackFallback;

                if (toSend != null)
                    RelayDiagnostics.Warning("Every screenshot attempt was black or failed; sending the last frame anyway (the screen may genuinely be black).");
            }

            if (toSend == null)
            {
                RelayDiagnostics.Error("Screenshot capture failed: no attempt produced an image.");
                yield break;
            }

            QueueScreenshotUpload(webhookUrl, playerName, toSend.Image, toSend.Extension, toSend.ContentType, toSend.FilenameSuffix + ModeFileSuffix(_screenshotMode));
        }
        finally
        {
            _screenshotInProgress = false;

            if (_screenshotPipeline != null)
            {
                // The CPU scratch buffers go at once; the render texture stays for a minute in case another screenshot
                // follows, then TickScreenshotIdleRelease frees it.
                _screenshotPipeline.ReleaseScratchBuffers();
                _screenshotIdleReleaseAt = Time.unscaledTime + ScreenshotIdleReleaseSeconds;
            }
        }
    }
}
