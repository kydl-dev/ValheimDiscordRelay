using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Low-level Discord webhook HTTP helpers shared by the screenshot and death-video paths.
/// </summary>
internal sealed partial class ClientCaptureService
{
    /// <summary>Set when the game is quitting; file uploads then refuse to start (see AbortActiveUploads).</summary>
    private static volatile bool _uploadsCancelled;
    private static readonly object UploadSync = new();
    private static readonly List<HttpWebRequest> ActiveUploads = new();

    /// <summary>
    /// Aborts every file upload that is in flight and stops new ones from starting. The blocked
    /// GetRequestStream/GetResponse calls on the worker threads then throw instead of waiting out their timeouts.
    /// Text-only webhook messages (SendWebhookJson) are deliberately not affected.
    /// </summary>
    private static void AbortActiveUploads()
    {
        _uploadsCancelled = true;

        lock (UploadSync)
        {
            foreach (HttpWebRequest request in ActiveUploads)
            {
                try { request.Abort(); }
                catch (Exception ex) { RelayDiagnostics.Debug("Could not abort upload during quit: " + ex.Message); }
            }
        }
    }

    private static void SendWebhookFile(
        string webhookUrl,
        string username,
        string filename,
        string contentType,
        byte[] file,
        string content = null,
        string avatarUrl = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new InvalidOperationException("Webhook URL is empty.");

        string boundary = "----ValheimDiscordRelay" + Guid.NewGuid().ToString("N");
        string avatarJson = string.IsNullOrWhiteSpace(avatarUrl)
            ? string.Empty
            : ",\"avatar_url\":\"" + ClientTextUtils.JsonEscape(avatarUrl) + "\"";

        string payload = "{\"username\":\"" + ClientTextUtils.JsonEscape(username) + "\"" +
                         avatarJson +
                         ",\"content\":\"" + ClientTextUtils.JsonEscape(content ?? string.Empty) +
                         "\",\"allowed_mentions\":{\"parse\":[]}}";

        byte[] prefix = Encoding.UTF8.GetBytes(
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"payload_json\"\r\n" +
            "Content-Type: application/json\r\n\r\n" +
            payload + "\r\n" +
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"files[0]\"; filename=\"" +
            ClientTextUtils.JsonEscape(filename) + "\"\r\n" +
            "Content-Type: " + contentType + "\r\n\r\n");

        byte[] suffix = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
        byte[] body = new byte[prefix.Length + file.Length + suffix.Length];

        Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
        Buffer.BlockCopy(file, 0, body, prefix.Length, file.Length);
        Buffer.BlockCopy(suffix, 0, body, prefix.Length + file.Length, suffix.Length);

        HttpSecurity.EnsureTls12();

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(webhookUrl);
        request.Method = "POST";
        request.ContentType = "multipart/form-data; boundary=" + boundary;
        request.UserAgent = "ValheimDiscordRelay/" + ValheimDiscordRelayPlugin.ModVersion;
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        request.ContentLength = body.Length;

        // Registered so a quit can abort it; checked under the same lock AbortActiveUploads uses, so a quit that
        // lands between creating the request and here still cancels it.
        lock (UploadSync)
        {
            if (_uploadsCancelled)
                throw new OperationCanceledException("Upload cancelled because the game is quitting.");

            ActiveUploads.Add(request);
        }

        try
        {
            using (Stream stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                if (status < 200 || status >= 300)
                    throw new InvalidOperationException("Discord webhook returned HTTP " + status + ".");
            }
        }
        finally
        {
            lock (UploadSync)
            {
                ActiveUploads.Remove(request);
            }
        }

        if (AdminConfig.LogSuccessfulSends.Value)
            RelayDiagnostics.Info("Uploaded " + filename + " to Discord.");
    }

    /// <summary>
    /// Runs a file upload and retries the SAME file when the failure is temporary, so a network or rate-limit problem
    /// no longer causes a pointless lower-quality re-encode. Retried: HTTP 429 (waits Retry-After), HTTP 5xx, and
    /// failures where the request never reached Discord (DNS, connect, send). Not retried: a timeout or dropped
    /// connection while waiting for the answer (Discord may already have posted the file, a retry would duplicate it),
    /// HTTP 4xx other than 429, and a quit in progress.
    /// </summary>
    /// <param name="upload">The upload to run (throws on failure).</param>
    /// <param name="tooLarge">True when Discord refused the file as too large (HTTP 413): the caller should encode a smaller one.</param>
    /// <returns>True when the upload succeeded.</returns>
    private static bool UploadWithRetry(Action upload, out bool tooLarge)
    {
        const int maxAttempts = 3;
        tooLarge = false;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                upload();
                return true;
            }
            catch (Exception ex)
            {
                if (_uploadsCancelled || ex is OperationCanceledException)
                    return false;

                int delayMs = 0;
                bool retry = false;
                WebException web = ex as WebException;

                if (web != null)
                {
                    HttpWebResponse response = web.Response as HttpWebResponse;
                    int status = response != null ? (int)response.StatusCode : 0;

                    if (status == 413)
                    {
                        tooLarge = true;
                        RelayDiagnostics.Warning("Discord refused the upload as too large (HTTP 413).");
                        return false;
                    }

                    if (status == 429)
                    {
                        retry = true;
                        delayMs = 2000;
                        double seconds;
                        string retryAfter = response.Headers["Retry-After"];
                        if (!string.IsNullOrWhiteSpace(retryAfter) &&
                            double.TryParse(retryAfter, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out seconds))
                            delayMs = Math.Max(500, Math.Min(15000, (int)(seconds * 1000.0)));
                    }
                    else if (status >= 500 && status <= 599)
                    {
                        retry = true;
                        delayMs = 3000;
                    }
                    else if (response == null &&
                             (web.Status == WebExceptionStatus.NameResolutionFailure ||
                              web.Status == WebExceptionStatus.ConnectFailure ||
                              web.Status == WebExceptionStatus.SendFailure))
                    {
                        retry = true;
                        delayMs = 3000;
                    }
                    else if (response == null && web.Status == WebExceptionStatus.Timeout)
                    {
                        RelayDiagnostics.Warning("Upload timed out while waiting for Discord; not retrying, because the file may already have been posted.");
                    }
                }

                if (!retry || attempt == maxAttempts)
                {
                    RelayDiagnostics.Warning("Upload failed" + (retry ? " after " + attempt + " attempts" : "") + ": " + ex.Message);
                    return false;
                }

                RelayDiagnostics.Warning("Upload failed (" + ex.Message + "); retrying the same file in " + delayMs + " ms (" + attempt + "/" + maxAttempts + ").");
                System.Threading.Thread.Sleep(delayMs);

                if (_uploadsCancelled)
                    return false;
            }
        }

        return false;
    }

    private static void SendWebhookJson(string webhookUrl, string username, string content, int timeoutMilliseconds = 15000)
    {
        string payload = "{\"username\":\"" + ClientTextUtils.JsonEscape(username) +
                         "\",\"content\":\"" + ClientTextUtils.JsonEscape(content) +
                         "\",\"allowed_mentions\":{\"parse\":[]}}";

        HttpSecurity.EnsureTls12();

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(webhookUrl);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.UserAgent = "ValheimDiscordRelay/" + ValheimDiscordRelayPlugin.ModVersion;
        request.Timeout = timeoutMilliseconds;
        request.ReadWriteTimeout = timeoutMilliseconds;

        byte[] data = Encoding.UTF8.GetBytes(payload);
        request.ContentLength = data.Length;

        using (Stream stream = request.GetRequestStream())
            stream.Write(data, 0, data.Length);

        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        {
            int status = (int)response.StatusCode;
            if (status < 200 || status >= 300)
                throw new InvalidOperationException("Discord webhook returned HTTP " + status + ".");
        }

        if (AdminConfig.LogSuccessfulSends.Value)
            RelayDiagnostics.Info("Sent a " + (string.IsNullOrEmpty(username) ? "" : username + " ") + "message to Discord.");
    }
}
