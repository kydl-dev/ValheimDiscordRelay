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
        /// <summary>
        /// How a captured frame is read back from the GPU.
        /// </summary>
        internal enum ReadbackStyle
        {
            /// <summary>GPU converts to RGB24 while reading back (the original path; what the rolling death-video frames use).</summary>
            Rgb24 = 0,

            /// <summary>Same, but the capture is first copied through an intermediate render texture of the same size.</summary>
            Rgb24ViaBlit = 1,

            /// <summary>
            /// Read the texture back in its own RGBA32 layout (no GPU-side format conversion) and drop the alpha channel
            /// on the CPU. Rules out the GPU's RGB24 conversion as the cause of a black frame.
            /// </summary>
            NativeRgba32 = 2
        }

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
        private byte[] _rgbaBuffer;

        private static bool _environmentLogged;

        internal ScreenCapturePipeline()
        {
            _asyncSupported = SystemInfo.supportsAsyncGPUReadback;
            LogEnvironmentOnce();
        }

        /// <summary>
        /// Logs the graphics environment once per game session (both pipeline instances share it), so a bug report that
        /// only contains the relay log still says which graphics API / GPU the capture ran on.
        /// </summary>
        private static void LogEnvironmentOnce()
        {
            if (_environmentLogged)
                return;

            _environmentLogged = true;

            if (!SystemInfo.supportsAsyncGPUReadback)
                RelayDiagnostics.Warning(
                    "AsyncGPUReadback is not supported on this system; falling back to synchronous capture readback.");

            RelayDiagnostics.Info(
                "Capture environment: api=" + SystemInfo.graphicsDeviceType +
                ", gpu=" + SystemInfo.graphicsDeviceName +
                ", driver=" + SystemInfo.graphicsDeviceVersion +
                ", asyncReadback=" + SystemInfo.supportsAsyncGPUReadback +
                ", colorSpace=" + QualitySettings.activeColorSpace +
                ", screen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode +
                ", unity=" + Application.unityVersion + ".");
        }

        /// <summary>
        /// Captures and (if needed) downscales the current frame; must be called right after WaitForEndOfFrame.
        /// </summary>
        /// <param name="targetWidth">Desired output width in pixels.</param>
        /// <param name="targetHeight">Desired output height in pixels.</param>
        /// <param name="onComplete">Called on the main thread with the resulting RGB24 bytes.</param>
        /// <param name="onFailure">Called instead if capture could not be started or the GPU reported an error.</param>
        /// <param name="style">
        /// How the frame is read back. <see cref="ReadbackStyle.Rgb24ViaBlit"/> copies the capture through an intermediate
        /// render texture even when the target is not smaller than the screen (the path every downscaled frame takes);
        /// <see cref="ReadbackStyle.NativeRgba32"/> reads the texture back unconverted and strips the alpha on the CPU.
        /// </param>
        internal void CaptureAsync(int targetWidth, int targetHeight, Action<byte[], int, int> onComplete, Action onFailure, ReadbackStyle style = ReadbackStyle.Rgb24)
        {
            try
            {
                bool forceBlit = style == ReadbackStyle.Rgb24ViaBlit;
                bool nativeRgba = style == ReadbackStyle.NativeRgba32;

                EnsureFullResTexture();
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullResTexture);

                int fullWidth = _fullResTexture.width;
                int fullHeight = _fullResTexture.height;

                bool downscale = targetWidth > 0 && targetHeight > 0 &&
                                  (targetWidth < fullWidth || targetHeight < fullHeight);

                if (!downscale)
                {
                    if (!forceBlit)
                    {
                        RequestReadback(_fullResTexture, fullWidth, fullHeight, onComplete, onFailure, nativeRgba);
                        return;
                    }

                    EnsureFitTexture(fullWidth, fullHeight);
                    Graphics.Blit(_fullResTexture, _fitTexture);
                    RequestReadback(_fitTexture, fullWidth, fullHeight, onComplete, onFailure, nativeRgba);
                    return;
                }

                int fitWidth, fitHeight;
                ComputeAspectFit(fullWidth, fullHeight, targetWidth, targetHeight, out fitWidth, out fitHeight);

                EnsureFitTexture(fitWidth, fitHeight);
                Graphics.Blit(_fullResTexture, _fitTexture);

                if (fitWidth == targetWidth && fitHeight == targetHeight)
                {
                    RequestReadback(_fitTexture, fitWidth, fitHeight, onComplete, onFailure, nativeRgba);
                    return;
                }

                int offsetX = (targetWidth - fitWidth) / 2;
                int offsetY = (targetHeight - fitHeight) / 2;

                RequestReadback(_fitTexture, fitWidth, fitHeight, (fitBytes, w, h) =>
                {
                    onComplete(Letterbox(fitBytes, w, h, targetWidth, targetHeight, offsetX, offsetY), targetWidth, targetHeight);
                }, onFailure, nativeRgba);
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
        /// <param name="nativeRgba">
        /// Read the texture back as RGBA32 (no GPU-side conversion) and convert to RGB24 on the CPU, flipping rows in the same pass.
        /// </param>
        private void RequestReadback(RenderTexture source, int width, int height, Action<byte[], int, int> onComplete, Action onFailure, bool nativeRgba = false)
        {
            if (_asyncSupported)
            {
                TextureFormat readbackFormat = nativeRgba ? TextureFormat.RGBA32 : TextureFormat.RGB24;

                AsyncGPUReadback.Request(source, 0, readbackFormat, request =>
                {
                    if (request.hasError)
                    {
                        RelayDiagnostics.Error("AsyncGPUReadback reported an error during screen capture.");
                        onFailure?.Invoke();
                        return;
                    }

                    NativeArray<byte> data = request.GetData<byte>();

                    if (nativeRgba)
                    {
                        if (data.Length != width * height * 4)
                        {
                            RelayDiagnostics.Error("Native RGBA32 readback returned " + data.Length + " bytes, expected " + (width * height * 4) + ".");
                            onFailure?.Invoke();
                            return;
                        }

                        byte[] rgba = GetBuffer(ref _rgbaBuffer, data.Length);
                        data.CopyTo(rgba);

                        byte[] rgb = GetBuffer(ref _readbackBuffer, width * height * 3);
                        ConvertRgba32ToRgb24(rgba, width, height, SystemInfo.graphicsUVStartsAtTop, rgb);
                        onComplete(rgb, width, height);
                        return;
                    }

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
        /// Converts tightly-packed RGBA32 to tightly-packed RGB24, dropping alpha, optionally reversing the row order.
        /// </summary>
        /// <param name="rgba">Source pixels, 4 bytes each.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="flipRows">Write the rows in reverse order (the same correction FlipRowsVertically applies).</param>
        /// <param name="rgb">Destination, exactly width * height * 3 bytes.</param>
        private static void ConvertRgba32ToRgb24(byte[] rgba, int width, int height, bool flipRows, byte[] rgb)
        {
            for (int row = 0; row < height; row++)
            {
                int sourceRow = flipRows ? height - 1 - row : row;
                int s = sourceRow * width * 4;
                int d = row * width * 3;

                for (int x = 0; x < width; x++)
                {
                    rgb[d] = rgba[s];
                    rgb[d + 1] = rgba[s + 1];
                    rgb[d + 2] = rgba[s + 2];
                    d += 3;
                    s += 4;
                }
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
        /// Brightness summary of a sampled frame, used to recognise a capture that came back black.
        /// </summary>
        internal struct FrameStats
        {
            /// <summary>Highest colour-channel value (0-255) among the sampled pixels.</summary>
            internal int Max;

            /// <summary>Mean colour-channel value (0-255) over the sampled pixels.</summary>
            internal double Mean;

            /// <summary>
            /// True when the frame is effectively black: nothing brighter than a barely-visible grey, or an average so low
            /// that at most a sliver of the screen has any content. A real Valheim frame always carries HUD elements and
            /// scenery well above both limits, even at night or in a cave.
            /// </summary>
            internal bool IsBlack
            {
                get { return Max <= BlackMaxChannel || Mean < BlackMeanChannel; }
            }

            public override string ToString()
            {
                return "max=" + Max + ", mean=" + Mean.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private const int BlackMaxChannel = 8;
        private const double BlackMeanChannel = 1.5;

        /// <summary>
        /// Measures a tightly-packed RGB24 buffer by sampling roughly one byte in 97 (cheap even for a 4K frame; 97 is
        /// coprime with 3, so the samples rotate through the R, G and B channels).
        /// </summary>
        /// <param name="rgb24">The RGB24 pixel bytes.</param>
        /// <returns>The sampled brightness summary.</returns>
        internal static FrameStats MeasureRgb24(byte[] rgb24)
        {
            const int stride = 97;
            int max = 0;
            long sum = 0;
            int count = 0;

            for (int i = 0; i < rgb24.Length; i += stride)
            {
                int value = rgb24[i];
                if (value > max)
                    max = value;

                sum += value;
                count++;
            }

            return new FrameStats { Max = max, Mean = count > 0 ? (double)sum / count : 0d };
        }

        /// <summary>
        /// Measures a Color32 pixel array (alpha ignored) by sampling roughly one pixel in 31.
        /// </summary>
        /// <param name="pixels">The pixels to sample.</param>
        /// <returns>The sampled brightness summary.</returns>
        internal static FrameStats MeasureColors(Color32[] pixels)
        {
            const int stride = 31;
            int max = 0;
            long sum = 0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += stride)
            {
                Color32 c = pixels[i];
                int brightest = c.r > c.g ? c.r : c.g;
                if (c.b > brightest)
                    brightest = c.b;

                if (brightest > max)
                    max = brightest;

                sum += c.r + c.g + c.b;
                count++;
            }

            return new FrameStats { Max = max, Mean = count > 0 ? sum / (3d * count) : 0d };
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

        /// <summary>True while the capture render texture exists (so the next capture is not the first use of a fresh texture).</summary>
        internal bool HasLiveTextures
        {
            get { return _fullResTexture != null && _fullResTexture.IsCreated(); }
        }

        /// <summary>
        /// Creates the capture texture if needed and captures into it once, with no readback. Must be called right after
        /// WaitForEndOfFrame. A freshly created render texture may not hold a usable first capture, so the real attempt
        /// is made a frame later, against a texture that has already been through one capture.
        /// </summary>
        internal void WarmUp()
        {
            try
            {
                EnsureFullResTexture();
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullResTexture);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Screenshot warm-up capture failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One-line description of the capture render textures (size, format, colour space), for the diagnostics log.
        /// </summary>
        internal string DescribeTextures()
        {
            if (_fullResTexture == null)
                return "captureTexture=none";

            string text = "captureTexture=" + _fullResTexture.width + "x" + _fullResTexture.height +
                          " format=" + _fullResTexture.format +
                          " graphicsFormat=" + _fullResTexture.graphicsFormat +
                          " sRGB=" + _fullResTexture.sRGB +
                          " msaa=" + _fullResTexture.antiAliasing;

            if (_fitTexture != null)
            {
                text += ", fitTexture=" + _fitWidth + "x" + _fitHeight +
                        " format=" + _fitTexture.format +
                        " sRGB=" + _fitTexture.sRGB;
            }

            return text;
        }

        /// <summary>
        /// Drops the CPU scratch buffers (a 4K frame is tens of MB each) but keeps the GPU textures alive.
        /// Only call this when no readback is in flight.
        /// </summary>
        internal void ReleaseScratchBuffers()
        {
            _readbackBuffer = null;
            _flipBuffer = null;
            _letterboxBuffer = null;
            _rgbaBuffer = null;
        }

        /// <summary>
        /// Frees the GPU textures and CPU scratch buffers. The pipeline stays usable: everything is recreated on the
        /// next capture. Only call this when no readback is in flight.
        /// </summary>
        internal void ReleaseResources()
        {
            ReleaseTexture(ref _fullResTexture);
            ReleaseTexture(ref _fitTexture);
            _fitWidth = 0;
            _fitHeight = 0;
            ReleaseScratchBuffers();
        }

        public void Dispose()
        {
            ReleaseResources();
        }
    }
}
