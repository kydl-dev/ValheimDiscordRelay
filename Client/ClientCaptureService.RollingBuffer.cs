using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The in-memory rolling pre/post-death frame buffer.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        /// <summary>
        /// One buffered frame, held in memory rather than written to disk continuously.
        /// </summary>
        private sealed class FrameEntry
        {
            public byte[] Data;
            public float Time;
        }

        private readonly List<FrameEntry> _frames = new();

        private IEnumerator CaptureRollingFrame()
        {
            _frameCaptureInProgress = true;
            yield return new WaitForEndOfFrame();

            int width, height;
            ClientConfig.TryGetDeathResolution(out width, out height);

            float capturedAt = Time.unscaledTime;

            // Capture never stops (see Tick); only pruning is paused, and only while a clip is still waiting for its
            // post-death frames. Once a clip has copied its window, processing/upload no longer touches the buffer.
            // The boss video is cut from this same buffer (see ClientCaptureService.BossVideo.cs).
            bool pruneAfterCapture = !_deathRecording && !_bossRecording;

            _capturePipeline.CaptureAsync(
                width,
                height,
                (rgb24, w, h) => OnRollingFrameCaptured(rgb24, w, h, capturedAt, pruneAfterCapture),
                () => { _frameCaptureInProgress = false; });
        }

        /// <summary>
        /// Encodes a captured frame to JPEG and appends it to the rolling buffer.
        /// </summary>
        /// <param name="rgb24">Raw RGB24 pixel bytes for the captured frame.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="capturedAt">Unscaled time at which the frame was captured.</param>
        /// <param name="pruneAfterCapture">Whether to prune old frames after appending this one.</param>
        private void OnRollingFrameCaptured(byte[] rgb24, int width, int height, float capturedAt, bool pruneAfterCapture)
        {
            try
            {
                EnsureFrameEncodeTexture(width, height);
                _frameEncodeTexture.LoadRawTextureData(rgb24);
                _frameEncodeTexture.Apply(false, false);

                byte[] jpg = ImageConversion.EncodeToJPG(_frameEncodeTexture, 85);
                if (jpg == null || jpg.Length == 0)
                    return;

                float pruneCutoff = capturedAt - 4.1f;

                lock (_sync)
                {
                    _frames.Add(new FrameEntry
                    {
                        Data = jpg,
                        Time = capturedAt
                    });

                    if (pruneAfterCapture)
                        PruneRollingFramesLocked(pruneCutoff);
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Death frame capture failed.", ex);
            }
            finally
            {
                _frameCaptureInProgress = false;
            }
        }

        private void EnsureFrameEncodeTexture(int width, int height)
        {
            if (_frameEncodeTexture != null && _frameEncodeWidth == width && _frameEncodeHeight == height)
                return;

            if (_frameEncodeTexture != null)
                UnityEngine.Object.Destroy(_frameEncodeTexture);

            _frameEncodeTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            _frameEncodeWidth = width;
            _frameEncodeHeight = height;
        }

        private void PruneRollingFramesLocked(float cutoff)
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (_frames[i].Time < cutoff)
                    _frames.RemoveAt(i);
            }
        }
    }
}
