using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

internal sealed partial class ClientCaptureService
{
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
