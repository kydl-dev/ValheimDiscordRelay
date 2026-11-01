using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    internal sealed partial class ClientCaptureService
    {
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
    }
}
