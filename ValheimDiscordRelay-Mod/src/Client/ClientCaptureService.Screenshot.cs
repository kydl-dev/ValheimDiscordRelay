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
            public ScreenCapturePipeline.FrameStats Stats;

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
        // Saved strategy preference (survives game restarts)
        // ---------------------------------------------------------------------------------------------------------

        private static string ScreenshotStrategyFilePath
        {
            get { return Path.Combine(BepInEx.Paths.CachePath, ScreenshotStrategyFileName); }
        }

        /// <summary>
        /// Identifies the graphics setup the saved strategy was found on. If the player changes GPU or graphics API the
        /// saved value no longer applies and detection starts over.
        /// </summary>
        private static string CurrentGraphicsSignature()
        {
            return SystemInfo.graphicsDeviceType + " | " + SystemInfo.graphicsDeviceName;
        }

        /// <summary>
        /// Loads the strategy that worked on this PC in an earlier session (if any) and makes it the first one tried.
        /// Called once at startup.
        /// </summary>
        private void LoadSavedScreenshotStrategy()
        {
            try
            {
                string path = ScreenshotStrategyFilePath;
                if (!File.Exists(path))
                {
                    RelayDiagnostics.Info("Screenshot capture: no saved strategy for this PC yet; starting with the " + _screenshotStrategy + " strategy.");
                    return;
                }

                string strategyText = null;
                string graphics = null;

                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();

                    if (key.Equals("strategy", StringComparison.OrdinalIgnoreCase))
                        strategyText = value;
                    else if (key.Equals("graphics", StringComparison.OrdinalIgnoreCase))
                        graphics = value;
                }

                ScreenshotStrategy parsed = ScreenshotStrategy.Native;
                bool valid = !string.IsNullOrEmpty(strategyText) &&
                             Enum.TryParse(strategyText, true, out parsed) &&
                             Array.IndexOf(CaptureStrategies, parsed) >= 0;

                if (!valid)
                {
                    RelayDiagnostics.Warning("Screenshot capture: ignoring the saved strategy file (unreadable strategy '" + strategyText + "'); starting with the " + _screenshotStrategy + " strategy.");
                    return;
                }

                string current = CurrentGraphicsSignature();
                if (!string.Equals(graphics, current, StringComparison.Ordinal))
                {
                    RelayDiagnostics.Info("Screenshot capture: the saved strategy (" + parsed + ") was found on '" + graphics + "' but this session runs on '" + current +
                                          "'; ignoring it and starting with the " + _screenshotStrategy + " strategy.");
                    return;
                }

                _screenshotStrategy = parsed;
                RelayDiagnostics.Info("Screenshot capture: starting with the " + parsed + " strategy saved for this PC (" + current + ").");
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Screenshot capture: could not read the saved strategy (" + ex.Message + "); starting with the " + _screenshotStrategy + " strategy.");
            }
        }

        private static void SaveScreenshotStrategy(ScreenshotStrategy strategy)
        {
            try
            {
                string text =
                    "# ValheimDiscordRelay: the screenshot capture strategy that worked on this PC. Delete this file to detect it again." + Environment.NewLine +
                    "strategy=" + strategy + Environment.NewLine +
                    "graphics=" + CurrentGraphicsSignature() + Environment.NewLine +
                    "saved=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine;

                File.WriteAllText(ScreenshotStrategyFilePath, text);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Screenshot capture: could not save the working strategy (" + ex.Message + "); it will be detected again next session.");
            }
        }

        /// <summary>
        /// The strategies for one screenshot: the preferred one, the other capture strategies in default order, then the buffered-frame fallback.
        /// </summary>
        private ScreenshotStrategy[] BuildScreenshotStrategyOrder()
        {
            ScreenshotStrategy[] order = new ScreenshotStrategy[CaptureStrategies.Length + 1];
            int next = 0;

            order[next++] = _screenshotStrategy;

            for (int i = 0; i < CaptureStrategies.Length; i++)
            {
                if (CaptureStrategies[i] != _screenshotStrategy)
                    order[next++] = CaptureStrategies[i];
            }

            order[next] = ScreenshotStrategy.BufferedFrame;
            return order;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Idle release of the screenshot render texture
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Frees the screenshot render texture once it has been idle for <see cref="ScreenshotIdleReleaseSeconds"/>
        /// (a 4K render texture is ~33 MB). Called every frame from Tick.
        /// </summary>
        private void TickScreenshotIdleRelease()
        {
            if (_screenshotIdleReleaseAt <= 0f || _screenshotInProgress || Time.unscaledTime < _screenshotIdleReleaseAt)
                return;

            _screenshotIdleReleaseAt = 0f;

            if (_screenshotPipeline != null)
            {
                _screenshotPipeline.ReleaseResources();
                RelayDiagnostics.Debug("[Screenshot] Released the idle screenshot render textures.");
            }
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
                if (string.IsNullOrWhiteSpace(ClientConfig.ScreenshotWebhookUrl.Value))
                {
                    RelayDiagnostics.Warning("Screenshot requested, but [Screenshots] Server - Webhook URL is empty.");
                    yield break;
                }

                string playerName = ClientConfig.IncludeScreenshotPlayerName.Value
                    ? ClientTextUtils.GetLocalPlayerName()
                    : "Valheim Player";

                string webhookUrl = ClientConfig.ScreenshotWebhookUrl.Value.Trim();

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

                QueueScreenshotUpload(webhookUrl, playerName, toSend.Image, toSend.Extension, toSend.ContentType, toSend.FilenameSuffix);
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
                attempt.Stats = ScreenCapturePipeline.MeasureRgb24(rgb24);
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
                attempt.Stats = ScreenCapturePipeline.MeasureColors(pixels);
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
                attempt.Stats = ScreenCapturePipeline.MeasureColors(decoded.GetPixels32());
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

        // ---------------------------------------------------------------------------------------------------------
        // Diagnostics context
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A one-line snapshot of the game state around a screenshot: is there a local player, is the game loading,
        /// teleporting or paused, and is the rolling capture alive. Distinguishes a screen that was genuinely black
        /// from a capture that went wrong.
        /// </summary>
        private string DescribeScreenshotContext()
        {
            string player;
            try
            {
                Player local = Player.m_localPlayer;
                player = local == null
                    ? "none (menu or loading)"
                    : "present (teleporting=" + local.IsTeleporting() + ", dead=" + local.IsDead() + ")";
            }
            catch (Exception ex)
            {
                player = "unknown (" + ex.GetType().Name + ")";
            }

            string world;
            try { world = ZNetScene.instance != null ? "loaded" : "not loaded"; }
            catch (Exception) { world = "unknown"; }

            string paused;
            try { paused = Game.IsPaused().ToString(); }
            catch (Exception) { paused = "unknown"; }

            float bufferedAge;
            FrameEntry buffered = GetNewestBufferedFrame(out bufferedAge);
            string bufferedText = buffered == null
                ? "none"
                : "age " + bufferedAge.ToString("0.00", CultureInfo.InvariantCulture) + "s";

            return "player=" + player +
                   ", world=" + world +
                   ", loadingScreen=" + DescribeLoadingScreen() +
                   ", paused=" + paused +
                   ", timeScale=" + Time.timeScale.ToString("0.##", CultureInfo.InvariantCulture) +
                   ", screen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode +
                   ", bufferedFrame=" + bufferedText;
        }

        /// <summary>
        /// Whether Valheim's loading screen is showing. Read by reflection (Hud.instance.m_loadingScreen) so a change in
        /// the game's UI classes can only make this say "unknown", never break the build or the screenshot.
        /// </summary>
        private static string DescribeLoadingScreen()
        {
            try
            {
                Type hudType = typeof(Player).Assembly.GetType("Hud");
                if (hudType == null)
                    return "unknown";

                const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                FieldInfo instanceField = hudType.GetField("instance", AnyStatic);
                object hud = instanceField != null ? instanceField.GetValue(null) : null;
                if (hud == null)
                    return "no HUD";

                FieldInfo screenField = hudType.GetField("m_loadingScreen", AnyInstance);
                object screen = screenField != null ? screenField.GetValue(hud) : null;
                if (screen == null)
                    return "unknown";

                GameObject go = screen as GameObject;
                if (go == null)
                {
                    Component component = screen as Component;
                    if (component != null)
                        go = component.gameObject;
                }

                if (go == null)
                    return "unknown";

                string state = go.activeInHierarchy ? "showing" : "hidden";

                PropertyInfo alpha = screen.GetType().GetProperty("alpha", AnyInstance);
                if (alpha != null && alpha.PropertyType == typeof(float))
                    state += " (alpha " + ((float)alpha.GetValue(screen, null)).ToString("0.00", CultureInfo.InvariantCulture) + ")";

                return state;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Encoding and upload
        // ---------------------------------------------------------------------------------------------------------

        private byte[] EncodeScreenshotPng(byte[] rgb24, int width, int height)
        {
            EnsureScreenshotEncodeTexture(width, height);
            _screenshotEncodeTexture.LoadRawTextureData(rgb24);
            _screenshotEncodeTexture.Apply(false, false);

            byte[] image = ImageConversion.EncodeToPNG(_screenshotEncodeTexture);
            if (image == null || image.Length == 0)
                throw new InvalidOperationException("PNG encoding returned no data.");

            return image;
        }

        private static void QueueScreenshotUpload(string webhookUrl, string playerName, byte[] image, string extension, string contentType, string filenameSuffix)
        {
            string filename = "valheim-screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + filenameSuffix + "." + extension;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SendWebhookFile(webhookUrl, playerName, filename, contentType, image);
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Error("Screenshot upload failed.", ex);
                }
            });
        }

        private void EnsureScreenshotEncodeTexture(int width, int height)
        {
            if (_screenshotEncodeTexture != null && _screenshotEncodeWidth == width && _screenshotEncodeHeight == height)
                return;

            if (_screenshotEncodeTexture != null)
                UnityEngine.Object.Destroy(_screenshotEncodeTexture);

            _screenshotEncodeTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            _screenshotEncodeWidth = width;
            _screenshotEncodeHeight = height;
        }
    }
}
