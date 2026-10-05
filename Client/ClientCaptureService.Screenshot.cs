using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The manual hotkey screenshot path.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        private IEnumerator CaptureManualScreenshot()
        {
            _screenshotInProgress = true;
            yield return new WaitForEndOfFrame();

            if (string.IsNullOrWhiteSpace(ClientConfig.ScreenshotWebhookUrl.Value))
            {
                RelayDiagnostics.Warning("Screenshot requested, but [Screenshots] Server - Webhook URL is empty.");
                _screenshotInProgress = false;
                yield break;
            }

            string playerName = ClientConfig.IncludeScreenshotPlayerName.Value
                ? ClientTextUtils.GetLocalPlayerName()
                : "Valheim Player";

            string webhookUrl = ClientConfig.ScreenshotWebhookUrl.Value.Trim();

            _capturePipeline.CaptureAsync(
                Screen.width,
                Screen.height,
                (rgb24, w, h) => OnManualScreenshotCaptured(rgb24, w, h, playerName, webhookUrl),
                () => { _screenshotInProgress = false; });
        }

        private void OnManualScreenshotCaptured(byte[] rgb24, int width, int height, string playerName, string webhookUrl)
        {
            try
            {
                EnsureScreenshotEncodeTexture(width, height);
                _screenshotEncodeTexture.LoadRawTextureData(rgb24);
                _screenshotEncodeTexture.Apply(false, false);

                byte[] image = ImageConversion.EncodeToPNG(_screenshotEncodeTexture);
                if (image == null || image.Length == 0)
                    return;

                string filename = "valheim-screenshot-" +
                                  DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        SendWebhookFile(webhookUrl, playerName, filename, "image/png", image);
                    }
                    catch (Exception ex)
                    {
                        RelayDiagnostics.Error("Screenshot upload failed.", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Manual screenshot capture failed.", ex);
            }
            finally
            {
                _screenshotInProgress = false;
            }
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
