using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Captures the composited backbuffer, downscaling on the GPU via AsyncGPUReadback when needed.
    /// </summary>
    internal sealed class ScreenCapturePipeline : IDisposable
    {
        private readonly bool _asyncSupported;
        private RenderTexture _fullResTexture;
        private RenderTexture _fitTexture;
        private int _fitWidth;
        private int _fitHeight;

        // Scratch buffers reused for every captured frame. A frame is 1-3 MB (more for a full-resolution screenshot), and
        // allocating two or three of them 20-30 times a second left Unity's GC with a constant stream of large garbage to
        // collect, which shows up as frame hitches. Every consumer of onComplete copies the bytes out synchronously (into
        // a Texture2D) and never keeps the array, and all callbacks run on the main thread, so reuse is safe. A buffer
        // is only reallocated when the required size changes (for example around a manual screenshot).
        private byte[] _readbackBuffer;
        private byte[] _flipBuffer;
        private byte[] _letterboxBuffer;

        internal ScreenCapturePipeline()
        {
            _asyncSupported = SystemInfo.supportsAsyncGPUReadback;
            if (!_asyncSupported)
                RelayDiagnostics.Warning(
                    "AsyncGPUReadback is not supported on this system; falling back to synchronous capture readback.");
        }

        /// <summary>
        /// Captures and (if needed) downscales the current frame; must be called right after WaitForEndOfFrame.
        /// </summary>
        /// <param name="targetWidth">Desired output width in pixels.</param>
        /// <param name="targetHeight">Desired output height in pixels.</param>
        /// <param name="onComplete">Called on the main thread with the resulting RGB24 bytes.</param>
        /// <param name="onFailure">Called instead if capture could not be started or the GPU reported an error.</param>
        internal void CaptureAsync(int targetWidth, int targetHeight, Action<byte[], int, int> onComplete, Action onFailure)
        {
            try
            {
                EnsureFullResTexture();
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullResTexture);

                int fullWidth = _fullResTexture.width;
                int fullHeight = _fullResTexture.height;

                bool downscale = targetWidth > 0 && targetHeight > 0 &&
                                  (targetWidth < fullWidth || targetHeight < fullHeight);

                if (!downscale)
                {
                    RequestReadback(_fullResTexture, fullWidth, fullHeight, onComplete, onFailure);
                    return;
                }

                int fitWidth, fitHeight;
                ComputeAspectFit(fullWidth, fullHeight, targetWidth, targetHeight, out fitWidth, out fitHeight);

                EnsureFitTexture(fitWidth, fitHeight);
                Graphics.Blit(_fullResTexture, _fitTexture);

                if (fitWidth == targetWidth && fitHeight == targetHeight)
                {
                    RequestReadback(_fitTexture, fitWidth, fitHeight, onComplete, onFailure);
                    return;
                }

                int offsetX = (targetWidth - fitWidth) / 2;
                int offsetY = (targetHeight - fitHeight) / 2;

                RequestReadback(_fitTexture, fitWidth, fitHeight, (fitBytes, w, h) =>
                {
                    onComplete(Letterbox(fitBytes, w, h, targetWidth, targetHeight, offsetX, offsetY), targetWidth, targetHeight);
                }, onFailure);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Screen capture failed.", ex);
                onFailure?.Invoke();
            }
        }

        /// <summary>
        /// Reads back a render texture, correcting AsyncGPUReadback's top-down layout on APIs where it applies.
        /// </summary>
        /// <param name="source">The render texture to read back.</param>
        /// <param name="width">Width of the source texture in pixels.</param>
        /// <param name="height">Height of the source texture in pixels.</param>
        /// <param name="onComplete">Called on the main thread with the resulting RGB24 bytes.</param>
        /// <param name="onFailure">Called instead if the GPU reported an error.</param>
        private void RequestReadback(RenderTexture source, int width, int height, Action<byte[], int, int> onComplete, Action onFailure)
        {
            if (_asyncSupported)
            {
                AsyncGPUReadback.Request(source, 0, TextureFormat.RGB24, request =>
                {
                    if (request.hasError)
                    {
                        RelayDiagnostics.Error("AsyncGPUReadback reported an error during screen capture.");
                        onFailure?.Invoke();
                        return;
                    }

                    NativeArray<byte> data = request.GetData<byte>();
                    byte[] managed = GetBuffer(ref _readbackBuffer, data.Length);
                    data.CopyTo(managed);

                    if (SystemInfo.graphicsUVStartsAtTop)
                        managed = FlipRowsVertically(managed, width, height);

                    onComplete(managed, width, height);
                });
            }
            else
            {
                ReadSynchronously(source, width, height, onComplete);
            }
        }

        /// <summary>
        /// Reverses the row order of a tightly-packed RGB24 buffer.
        /// </summary>
        /// <param name="data">The source RGB24 pixel bytes.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <returns>A reused byte array (valid until the next capture) with rows in reversed order.</returns>
        private byte[] FlipRowsVertically(byte[] data, int width, int height)
        {
            int rowBytes = width * 3;
            byte[] flipped = GetBuffer(ref _flipBuffer, data.Length);

            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(
                    data, row * rowBytes,
                    flipped, (height - 1 - row) * rowBytes,
                    rowBytes);
            }

            return flipped;
        }

        /// <summary>
        /// Centers a fitWidth x fitHeight RGB24 image inside a black targetWidth x targetHeight canvas.
        /// </summary>
        /// <param name="fitBytes">The source RGB24 pixel bytes to center.</param>
        /// <param name="fitWidth">Source image width in pixels.</param>
        /// <param name="fitHeight">Source image height in pixels.</param>
        /// <param name="targetWidth">Output canvas width in pixels.</param>
        /// <param name="targetHeight">Output canvas height in pixels.</param>
        /// <param name="offsetX">Horizontal offset at which to place the source image.</param>
        /// <param name="offsetY">Vertical offset at which to place the source image.</param>
        /// <returns>The letterboxed RGB24 image.</returns>
        private byte[] Letterbox(byte[] fitBytes, int fitWidth, int fitHeight, int targetWidth, int targetHeight, int offsetX, int offsetY)
        {
            byte[] result = GetBuffer(ref _letterboxBuffer, targetWidth * targetHeight * 3);

            // A reused buffer still holds the previous frame; the borders have to be black again.
            Array.Clear(result, 0, result.Length);

            int fitRowBytes = fitWidth * 3;
            int targetRowBytes = targetWidth * 3;
            int columnOffsetBytes = offsetX * 3;

            for (int row = 0; row < fitHeight; row++)
            {
                Buffer.BlockCopy(
                    fitBytes, row * fitRowBytes,
                    result, (row + offsetY) * targetRowBytes + columnOffsetBytes,
                    fitRowBytes);
            }

            return result;
        }

        /// <summary>
        /// Computes the largest size that fits inside the target box while preserving aspect ratio.
        /// </summary>
        /// <param name="sourceWidth">Source image width in pixels.</param>
        /// <param name="sourceHeight">Source image height in pixels.</param>
        /// <param name="targetWidth">Target box width in pixels.</param>
        /// <param name="targetHeight">Target box height in pixels.</param>
        /// <param name="fitWidth">Resulting width that fits within the target box.</param>
        /// <param name="fitHeight">Resulting height that fits within the target box.</param>
        private static void ComputeAspectFit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, out int fitWidth, out int fitHeight)
        {
            float sourceAspect = (float)sourceWidth / sourceHeight;
            float targetAspect = (float)targetWidth / targetHeight;

            if (sourceAspect > targetAspect)
            {
                fitWidth = targetWidth;
                fitHeight = Mathf.Clamp(Mathf.RoundToInt(targetWidth / sourceAspect), 1, targetHeight);
            }
            else
            {
                fitHeight = targetHeight;
                fitWidth = Mathf.Clamp(Mathf.RoundToInt(targetHeight * sourceAspect), 1, targetWidth);
            }
        }

        private static void ReadSynchronously(RenderTexture source, int width, int height, Action<byte[], int, int> onComplete)
        {
            Texture2D temp = null;
            RenderTexture previous = RenderTexture.active;

            try
            {
                temp = new Texture2D(width, height, TextureFormat.RGB24, false);
                RenderTexture.active = source;
                temp.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                temp.Apply(false, false);

                onComplete(temp.GetRawTextureData(), width, height);
            }
            finally
            {
                RenderTexture.active = previous;
                if (temp != null)
                    UnityEngine.Object.Destroy(temp);
            }
        }

        /// <summary>
        /// Returns <paramref name="buffer"/> if it is exactly <paramref name="length"/> bytes (Texture2D.LoadRawTextureData
        /// requires an exact size), otherwise replaces it with a new array of that size.
        /// </summary>
        private static byte[] GetBuffer(ref byte[] buffer, int length)
        {
            if (buffer == null || buffer.Length != length)
                buffer = new byte[length];

            return buffer;
        }

        private void EnsureFullResTexture()
        {
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);

            if (_fullResTexture != null && _fullResTexture.width == width && _fullResTexture.height == height)
                return;

            ReleaseTexture(ref _fullResTexture);

            _fullResTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _fullResTexture.Create();
        }

        private void EnsureFitTexture(int width, int height)
        {
            if (_fitTexture != null && _fitWidth == width && _fitHeight == height)
                return;

            ReleaseTexture(ref _fitTexture);

            _fitTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _fitTexture.Create();
            _fitWidth = width;
            _fitHeight = height;
        }

        private static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;

            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        public void Dispose()
        {
            ReleaseTexture(ref _fullResTexture);
            ReleaseTexture(ref _fitTexture);
            _readbackBuffer = null;
            _flipBuffer = null;
            _letterboxBuffer = null;
        }
    }
}
