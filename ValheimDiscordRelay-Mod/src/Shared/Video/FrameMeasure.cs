using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    internal static class FrameMeasure
    {
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
    }
}
