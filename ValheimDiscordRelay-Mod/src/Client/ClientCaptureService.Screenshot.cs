using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
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

        /// <summary>
        /// Starts one capture attempt with the given strategy. Sets <see cref="ScreenshotAttempt.Done"/> when the result
        /// (or a failure) is known: immediately for the synchronous and buffered-frame strategies, a few frames later for the async ones.
        /// </summary>
        /// <param name="strategy">How to obtain the frame.</param>
        /// <param name="attempt">Receives the outcome.</param>
        /// <param name="encodeEvenIfBlack">Encode a PNG even when the frame is black (used for the last real capture attempt).</param>
        private void StartScreenshotAttempt(ScreenshotStrategy strategy, ScreenshotAttempt attempt, bool encodeEvenIfBlack)
        {
            try
            {
                if (strategy == ScreenshotStrategy.Synchronous)
                {
                    CaptureScreenshotSynchronously(attempt, encodeEvenIfBlack);
                    return;
                }

                if (strategy == ScreenshotStrategy.BufferedFrame)
                {
                    if (_screenshotMode != ScreenshotMode.Normal)
                    {
                        // The buffered death-video frames still show the HUD and the player.
                        attempt.Failed = true;
                        attempt.Note = "buffered frames are not used for HUD-free screenshots";
                        attempt.Done = true;
                        return;
                    }

                    CaptureScreenshotFromBufferedFrame(attempt);
                    return;
                }

                if (_screenshotPipeline == null)
                    _screenshotPipeline = new ScreenCapturePipeline();

                ScreenCapturePipeline.ReadbackStyle style = ScreenCapturePipeline.ReadbackStyle.Rgb24;
                if (strategy == ScreenshotStrategy.Native)
                    style = ScreenCapturePipeline.ReadbackStyle.NativeRgba32;
                else if (strategy == ScreenshotStrategy.Blit)
                    style = ScreenCapturePipeline.ReadbackStyle.Rgb24ViaBlit;

                _screenshotPipeline.CaptureAsync(
                    Screen.width,
                    Screen.height,
                    (rgb24, w, h) => OnScreenshotAttemptCaptured(attempt, rgb24, w, h, encodeEvenIfBlack),
                    () => { if (!attempt.Abandoned) { attempt.Failed = true; attempt.Done = true; } },
                    style);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Screenshot attempt (" + strategy + ") could not start.", ex);
                attempt.Failed = true;
                attempt.Done = true;
            }
        }

        /// <summary>
        /// Readback callback for the async strategies (main thread). Judges the frame and, if it is usable, encodes it.
        /// </summary>
        private void OnScreenshotAttemptCaptured(ScreenshotAttempt attempt, byte[] rgb24, int width, int height, bool encodeEvenIfBlack)
        {
            if (attempt.Abandoned)
                return;

            try
            {
                attempt.Width = width;
                attempt.Height = height;
                attempt.Stats = FrameMeasure.MeasureRgb24(rgb24);
                attempt.Black = attempt.Stats.IsBlack;

                if (!attempt.Black || encodeEvenIfBlack)
                    attempt.Image = EncodeScreenshotPng(rgb24, width, height);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Manual screenshot capture failed.", ex);
                attempt.Failed = true;
            }
            finally
            {
                attempt.Done = true;
            }
        }

        /// <summary>
        /// Unity's own synchronous screenshot. It blocks the game for a moment (longer at 4K), which is why it only runs
        /// after the GPU-readback strategies came back black or failed.
        /// </summary>
        private void CaptureScreenshotSynchronously(ScreenshotAttempt attempt, bool encodeEvenIfBlack)
        {
            Texture2D captured = null;
            Texture2D opaque = null;

            try
            {
                captured = ScreenCapture.CaptureScreenshotAsTexture();
                if (captured == null)
                {
                    attempt.Failed = true;
                    return;
                }

                attempt.Width = captured.width;
                attempt.Height = captured.height;

                Color32[] pixels = captured.GetPixels32();
                attempt.Stats = FrameMeasure.MeasureColors(pixels);
                attempt.Black = attempt.Stats.IsBlack;

                if (attempt.Black && !encodeEvenIfBlack)
                    return;

                // Rebuilt as RGB24 so the PNG can never carry a (possibly zero) alpha channel, which Discord would
                // show as transparent.
                opaque = new Texture2D(captured.width, captured.height, TextureFormat.RGB24, false);
                opaque.SetPixels32(pixels);
                opaque.Apply(false, false);

                byte[] png = ImageConversion.EncodeToPNG(opaque);
                if (png == null || png.Length == 0)
                    attempt.Failed = true;
                else
                    attempt.Image = png;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Synchronous screenshot capture failed.", ex);
                attempt.Failed = true;
            }
            finally
            {
                if (captured != null)
                    UnityEngine.Object.Destroy(captured);

                if (opaque != null)
                    UnityEngine.Object.Destroy(opaque);

                attempt.Done = true;
            }
        }

        /// <summary>
        /// The last resort: the newest frame of the rolling death-video buffer. It is at most the death-video resolution
        /// (480x270 to 960x540) and is sent as the JPEG it already is, but it is a real picture, and it comes from the
        /// capture path that is known to work. Only used when it is recent and not black itself.
        /// </summary>
        private void CaptureScreenshotFromBufferedFrame(ScreenshotAttempt attempt)
        {
            Texture2D decoded = null;

            try
            {
                float age;
                FrameEntry newest = GetNewestBufferedFrame(out age);

                if (newest == null || newest.Data == null)
                {
                    attempt.Failed = true;
                    attempt.Note = "no buffered death-video frame (death-video recording is off or has not captured yet)";
                    return;
                }

                if (age > BufferedFrameMaxAgeSeconds)
                {
                    attempt.Failed = true;
                    attempt.Note = "the newest buffered frame is " + age.ToString("0.0", CultureInfo.InvariantCulture) + " s old";
                    return;
                }

                decoded = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!ImageConversionCompat.LoadImage(decoded, newest.Data, false))
                {
                    attempt.Failed = true;
                    attempt.Note = "the buffered frame could not be decoded";
                    return;
                }

                attempt.Width = decoded.width;
                attempt.Height = decoded.height;
                attempt.Stats = FrameMeasure.MeasureColors(decoded.GetPixels32());
                attempt.Black = attempt.Stats.IsBlack;

                if (attempt.Black)
                    return;

                attempt.Image = newest.Data;
                attempt.Extension = "jpg";
                attempt.ContentType = "image/jpeg";
                attempt.FilenameSuffix = "-buffered";
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Buffered-frame screenshot fallback failed.", ex);
                attempt.Failed = true;
            }
            finally
            {
                if (decoded != null)
                    UnityEngine.Object.Destroy(decoded);

                attempt.Done = true;
            }
        }

        /// <summary>
        /// The newest frame of the rolling buffer, or null if it is empty.
        /// </summary>
        /// <param name="ageSeconds">How long ago the frame was captured.</param>
        private FrameEntry GetNewestBufferedFrame(out float ageSeconds)
        {
            ageSeconds = 0f;

            lock (_sync)
            {
                FrameEntry newest = null;

                for (int i = 0; i < _frames.Count; i++)
                {
                    if (newest == null || _frames[i].Time > newest.Time)
                        newest = _frames[i];
                }

                if (newest != null)
                    ageSeconds = Time.unscaledTime - newest.Time;

                return newest;
            }
        }

    }
}
