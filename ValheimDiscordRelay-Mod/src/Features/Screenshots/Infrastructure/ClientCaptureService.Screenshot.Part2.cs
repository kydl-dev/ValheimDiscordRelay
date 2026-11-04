using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>Part 2 of ClientCaptureService (split out so the class stays under the size limit).</summary>
internal sealed partial class ClientCaptureService
{
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
