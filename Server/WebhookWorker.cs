using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Owns the background thread that POSTs relayed messages to Discord webhooks.
    /// </summary>
    internal sealed class WebhookWorker
    {
        private sealed class Item
        {
            public string WebhookUrl;
            public string Username;
            public string Content;

            /// <summary>When set, sent as-is instead of building a chat payload from Username/Content.</summary>
            public string PayloadJson;

            /// <summary>Human-readable description used in log lines for PayloadJson items.</summary>
            public string Label;
        }

        private readonly Queue<Item> _queue = new();
        private readonly object _sync = new();
        private readonly AutoResetEvent _signal = new(false);
        private Thread _thread;
        private volatile bool _stopping;

        public void Start()
        {
            _stopping = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ValheimDiscordRelay.Webhook"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _signal.Set();

            Thread thread = _thread;
            if (thread != null && thread.IsAlive)
                thread.Join(1500);

            _thread = null;

            lock (_sync)
                _queue.Clear();
        }

        public void Enqueue(string webhookUrl, string username, string content)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                RelayDiagnostics.Warning("No webhook is configured for this chat type; message was not sent.");
                return;
            }


            lock (_sync)
            {
                while (_queue.Count >= ServerConfig.QueueLimit.Value)
                    _queue.Dequeue();

                _queue.Enqueue(new Item
                {
                    WebhookUrl = webhookUrl,
                    Username = username,
                    Content = content
                });
            }

            _signal.Set();
        }

        /// <summary>
        /// Queues a ready-made webhook JSON payload (for example one carrying an embed).
        /// </summary>
        /// <param name="webhookUrl">The Discord webhook URL to post to.</param>
        /// <param name="payloadJson">The complete JSON body.</param>
        /// <param name="label">Short description for log lines, e.g. "boss death (Eikthyr)".</param>
        /// <returns>True if the payload was queued; false if no webhook URL was given.</returns>
        public bool EnqueueJson(string webhookUrl, string payloadJson, string label)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                RelayDiagnostics.Warning("No webhook is configured for " + label + "; message was not sent.");
                return false;
            }

            lock (_sync)
            {
                while (_queue.Count >= ServerConfig.QueueLimit.Value)
                    _queue.Dequeue();

                _queue.Enqueue(new Item
                {
                    WebhookUrl = webhookUrl,
                    PayloadJson = payloadJson,
                    Label = label
                });
            }

            _signal.Set();
            return true;
        }

        /// <summary>
        /// Sends a message immediately on the calling thread, bypassing the queue.
        /// </summary>
        /// <param name="webhookUrl">The Discord webhook URL to post to.</param>
        /// <param name="username">The Discord display name to post as.</param>
        /// <param name="content">The message content.</param>
        /// <param name="timeoutMs">Per-attempt HTTP timeout in milliseconds.</param>
        /// <returns>True only on a confirmed 2xx response.</returns>
        /// <param name="label">Optional short description for the success log line, e.g. "server notification (up)".</param>
        public bool SendNow(string webhookUrl, string username, string content, int timeoutMs, string label = null)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                RelayDiagnostics.Warning("No webhook is configured for this chat type; message was not sent.");
                return false;
            }

            Item item = new Item { WebhookUrl = webhookUrl, Username = username, Content = content, Label = label };

            const int maxAttempts = 2;
            const int maxRetryDelayMs = 1500;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                SendOutcome outcome = Send(item, timeoutMs);
                if (outcome.Success)
                    return true;

                if (outcome.StopRetrying || attempt >= maxAttempts)
                    return false;

                int delay = Math.Min(maxRetryDelayMs, outcome.RetryDelayMs ?? maxRetryDelayMs);
                Thread.Sleep(delay);
            }

            return false;
        }

        private void Run()
        {
            while (!_stopping)
            {
                Item item = null;

                lock (_sync)
                {
                    if (_queue.Count > 0)
                        item = _queue.Dequeue();
                }

                if (item == null)
                {
                    _signal.WaitOne(1000);
                    continue;
                }

                SendWithRetries(item);
                Thread.Sleep(Math.Max(50, ServerConfig.SendIntervalMs.Value));
            }
        }

        private void SendWithRetries(Item item)
        {
            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts && !_stopping; attempt++)
            {
                SendOutcome outcome = Send(item, 10000);
                if (outcome.Success)
                    return;

                if (outcome.StopRetrying)
                    return;

                int delay = outcome.RetryDelayMs ?? Math.Min(30000, 1000 * (1 << Math.Min(attempt - 1, 4)));
                if (attempt < maxAttempts)
                {
                    RelayDiagnostics.Warning("Retrying Discord webhook delivery in " + delay + " ms.");
                    Thread.Sleep(delay);
                }
            }
        }

        private struct SendOutcome
        {
            public bool Success;
            public bool StopRetrying;
            public int? RetryDelayMs;
        }

        /// <summary>
        /// Performs a single HTTP POST attempt to a Discord webhook.
        /// </summary>
        /// <param name="item">The webhook URL, username and content to send.</param>
        /// <param name="timeoutMs">HTTP timeout in milliseconds.</param>
        /// <returns>The outcome: success, a retry delay, or a fatal stop.</returns>
        private SendOutcome Send(Item item, int timeoutMs)
        {
            HttpWebResponse response = null;
            try
            {
                string payload = item.PayloadJson ?? DiscordFormatting.BuildWebhookJson(item.Username, item.Content);

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(item.WebhookUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = "ValheimDiscordRelay/" + ServerPlugin.PluginVersion;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;

                byte[] data = Encoding.UTF8.GetBytes(payload);
                request.ContentLength = data.Length;

                using (Stream stream = request.GetRequestStream())
                    stream.Write(data, 0, data.Length);

                response = (HttpWebResponse)request.GetResponse();

                int status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    if (AdminConfig.LogSuccessfulSends.Value)
                        RelayDiagnostics.Info(item.Label != null
                            ? "Relayed " + item.Label + " to Discord."
                            : "Relayed " + (string.IsNullOrEmpty(item.Username) ? "a" : item.Username) + " chat message to Discord.");
                    return new SendOutcome { Success = true };
                }

                RelayDiagnostics.Warning("Discord webhook returned HTTP " + status + ".");
                return new SendOutcome();
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                int status = response != null ? (int)response.StatusCode : 0;

                if (status == 429)
                {
                    int delay = GetRetryAfterMilliseconds(response);
                    RelayDiagnostics.Warning("Discord webhook rate limited the relay; waiting " + delay + " ms.");
                    return new SendOutcome { RetryDelayMs = delay };
                }

                if (status >= 500 && status <= 599)
                {
                    RelayDiagnostics.Warning("Discord webhook returned HTTP " + status + ".");
                    return new SendOutcome();
                }

                RelayDiagnostics.Error("Discord webhook request failed.", ex);
                return new SendOutcome { StopRetrying = true };
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Discord webhook request failed.", ex);
                return new SendOutcome { StopRetrying = true };
            }
            finally
            {
                if (response != null)
                    response.Dispose();
            }
        }

        private static int GetRetryAfterMilliseconds(HttpWebResponse response)
        {
            if (response == null)
                return 2000;

            string retryAfter = response.Headers["Retry-After"];
            double seconds;

            if (!string.IsNullOrWhiteSpace(retryAfter) &&
                double.TryParse(retryAfter, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds))
            {
                return Math.Max(500, Math.Min(30000, (int)(seconds * 1000.0)));
            }

            return 2000;
        }
    }
}
