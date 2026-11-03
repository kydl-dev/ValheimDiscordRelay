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

            /// <summary>
            /// Called once with the final outcome: true when Discord accepted the message, false when it was given up on or
            /// dropped. Runs on the worker thread (or on the thread that dropped the item), so it must only set flags.
            /// </summary>
            public Action<bool> OnComplete;

            /// <summary>
            /// True for messages that must not be lost to the queue limit (the weekly report's parts): they are never
            /// evicted and do not count towards [Chat] Server - Queue Limit.
            /// </summary>
            public bool ExemptFromLimit;

            /// <summary>True once the chat line was resent under the neutral sender name (Discord refused the player name).</summary>
            public bool NameFallbackUsed;
        }

        // Oldest first. A list rather than a Queue because a full queue evicts the oldest item that is NOT exempt, which can
        // sit behind exempt ones.
        private readonly List<Item> _queue = new();
        private readonly object _sync = new();
        private readonly AutoResetEvent _signal = new(false);
        private Thread _thread;
        private volatile bool _stopping;

        /// <summary>Set by Stop(drainMs): keep sending what is queued, then exit (until _drainDeadline).</summary>
        private volatile bool _draining;
        private int _drainDeadline;

        /// <summary>True while the worker thread holds an item it has taken from the queue and is not finished with. Guarded by _sync.</summary>
        private bool _busy;

        public void Start()
        {
            _stopping = false;
            _draining = false; // a restart in the same process (a new server session) must accept and send messages again
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ValheimDiscordRelay.Webhook"
            };
            _thread.Start();
        }

        /// <summary>
        /// Stops the worker thread. The messages still queued are dropped (their callbacks get false) unless a drain time is given.
        /// </summary>
        /// <param name="drainMs">How long, at most, to keep sending the queued messages before giving up. 0 = drop them at once.</param>
        public void Stop(int drainMs = 0)
        {
            Thread thread = _thread;
            bool alive = thread != null && thread.IsAlive;

            if (drainMs > 0 && alive)
            {
                // The worker keeps going until the queue is empty or the deadline passes; it never starts a retry it cannot finish.
                _drainDeadline = unchecked(Environment.TickCount + drainMs);
                _draining = true;
                _signal.Set();
                thread.Join(drainMs + 250);
            }

            _stopping = true;
            _signal.Set();

            if (thread != null && thread.IsAlive)
                thread.Join(drainMs > 0 ? 100 : 1500);

            _thread = null;

            List<Item> dropped = new List<Item>();
            lock (_sync)
            {
                dropped.AddRange(_queue);
                _queue.Clear();
            }

            foreach (Item item in dropped)
                Complete(item, false);
        }

        private static void Complete(Item item, bool success)
        {
            Action<bool> callback = item.OnComplete;
            if (callback == null)
                return;

            item.OnComplete = null;

            try { callback(success); }
            catch (Exception ex) { RelayDiagnostics.Warning("A delivery callback failed: " + ex.Message); }
        }

        public void Enqueue(string webhookUrl, string username, string content)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                RelayDiagnostics.Warning("No webhook is configured for this chat type; message was not sent.");
                return;
            }


            List<Item> overflow = null;
            lock (_sync)
            {
                MakeRoomLocked(ref overflow);

                _queue.Add(new Item
                {
                    WebhookUrl = webhookUrl,
                    Username = username,
                    Content = content
                });
            }

            CompleteAll(overflow);
            _signal.Set();
        }

        /// <summary>
        /// Makes room for one more message: while the queue holds as many limited (non-exempt) messages as the limit
        /// allows, the oldest of those is taken out and added to <paramref name="overflow"/>. Caller must hold _sync.
        /// </summary>
        private void MakeRoomLocked(ref List<Item> overflow)
        {
            int limit = ServerConfig.QueueLimit.Value;

            int limited = 0;
            foreach (Item queued in _queue)
            {
                if (!queued.ExemptFromLimit)
                    limited++;
            }

            while (limited >= limit)
            {
                int index = _queue.FindIndex(IsLimited);
                if (index < 0)
                    break;

                if (overflow == null)
                    overflow = new List<Item>();

                overflow.Add(_queue[index]);
                _queue.RemoveAt(index);
                limited--;
            }
        }

        private static bool IsLimited(Item item)
        {
            return !item.ExemptFromLimit;
        }

        private static void CompleteAll(List<Item> items)
        {
            if (items == null)
                return;

            foreach (Item item in items)
                Complete(item, false);
        }

        /// <summary>
        /// Queues a ready-made webhook JSON payload (for example one carrying an embed).
        /// </summary>
        /// <param name="webhookUrl">The Discord webhook URL to post to.</param>
        /// <param name="payloadJson">The complete JSON body.</param>
        /// <param name="label">Short description for log lines, e.g. "boss death (Eikthyr)".</param>
        /// <param name="onComplete">Optional. Called once with true when Discord accepted the message, false when it could not be delivered.</param>
        /// <param name="exemptFromLimit">
        /// True for a message that must not be dropped because the queue is full (the weekly report's parts, up to 24 at
        /// once). It does not count towards [Chat] Server - Queue Limit and is never evicted by it.
        /// </param>
        /// <returns>True if the payload was queued; false if no webhook URL was given.</returns>
        public bool EnqueueJson(string webhookUrl, string payloadJson, string label, Action<bool> onComplete = null, bool exemptFromLimit = false)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                RelayDiagnostics.Warning("No webhook is configured for " + label + "; message was not sent.");
                return false;
            }

            List<Item> overflow = null;
            lock (_sync)
            {
                // An exempt message is not counted and never evicts anything: the limit is for everything else.
                if (!exemptFromLimit)
                    MakeRoomLocked(ref overflow);

                _queue.Add(new Item
                {
                    WebhookUrl = webhookUrl,
                    PayloadJson = payloadJson,
                    Label = label,
                    OnComplete = onComplete,
                    ExemptFromLimit = exemptFromLimit
                });
            }

            CompleteAll(overflow);
            _signal.Set();
            return true;
        }

        /// <summary>
        /// Waits until everything queued so far has been sent (or given up on), for at most <paramref name="timeoutMs"/>.
        /// Used at server shutdown so the last messages are not lost when the process exits. Messages queued by others while
        /// waiting are waited for too.
        /// </summary>
        /// <param name="timeoutMs">The longest time to wait on the calling thread.</param>
        /// <returns>True if the queue was empty and nothing was being sent when the wait ended.</returns>
        public bool WaitUntilIdle(int timeoutMs)
        {
            int deadline = unchecked(Environment.TickCount + Math.Max(0, timeoutMs));

            while (true)
            {
                bool idle;
                lock (_sync)
                    idle = _queue.Count == 0 && !_busy;

                if (idle)
                    return true;

                Thread thread = _thread;
                if (thread == null || !thread.IsAlive || unchecked(Environment.TickCount - deadline) >= 0)
                    return false;

                Thread.Sleep(25);
            }
        }

        private void Run()
        {
            while (!_stopping)
            {
                Item item = null;

                lock (_sync)
                {
                    if (_queue.Count > 0)
                    {
                        item = _queue[0];
                        _queue.RemoveAt(0);
                        _busy = true;
                    }
                }

                if (item == null)
                {
                    // Draining and nothing left to send: Stop() is waiting for this thread to end.
                    if (_draining)
                        break;

                    _signal.WaitOne(1000);
                    continue;
                }

                try
                {
                    Complete(item, SendWithRetries(item));
                }
                finally
                {
                    lock (_sync)
                        _busy = false;
                }

                if (_draining)
                {
                    // No pacing sleep after the last message, and no new message once the time is up.
                    bool more;
                    lock (_sync)
                        more = _queue.Count > 0;

                    if (!more || DrainTimeLeftMs() <= 0)
                        break;
                }

                Thread.Sleep(Math.Max(50, ServerConfig.SendIntervalMs.Value));
            }
        }

        /// <summary>Milliseconds left of the drain time given to Stop(); int.MaxValue when not draining.</summary>
        private int DrainTimeLeftMs()
        {
            if (!_draining)
                return int.MaxValue;

            return unchecked(_drainDeadline - Environment.TickCount);
        }

        /// <returns>True only when Discord confirmed the message with a 2xx answer.</returns>
        private bool SendWithRetries(Item item)
        {
            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts && !_stopping; attempt++)
            {
                // During a shutdown drain, a request is only started while there is time left for it to answer.
                int timeoutMs = 10000;
                if (_draining)
                {
                    int left = DrainTimeLeftMs();
                    if (left <= 0)
                        return false;

                    timeoutMs = Math.Max(1000, Math.Min(timeoutMs, left));
                }

                SendOutcome outcome = Send(item, timeoutMs);
                if (outcome.Success)
                    return true;

                if (outcome.StopRetrying)
                    return false;

                int delay = outcome.RetryDelayMs ?? Math.Min(30000, 1000 * (1 << Math.Min(attempt - 1, 4)));
                if (attempt < maxAttempts)
                {
                    // A wait that would use up the drain time is pointless: the message is given up on now.
                    if (_draining && delay >= DrainTimeLeftMs())
                        return false;

                    RelayDiagnostics.Warning("Retrying Discord webhook delivery in " + delay + " ms.");
                    Thread.Sleep(delay);
                }
            }

            return false;
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

                HttpSecurity.EnsureTls12();

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(item.WebhookUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = "ValheimDiscordRelay/" + ValheimDiscordRelayPlugin.ModVersion;
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

                // HTTP 400 on a chat line: Discord refuses sender names that contain "discord" or "clyde". Resend once under a
                // neutral name with the player name in the text, instead of losing the message.
                string fallbackName, fallbackContent;
                if (status == 400 && item.PayloadJson == null && !item.NameFallbackUsed &&
                    DiscordNameFallback.TryBuild(item.Username, item.Content, out fallbackName, out fallbackContent))
                {
                    RelayDiagnostics.Warning("Discord refused the sender name '" + item.Username + "' (HTTP 400); resending the message once under '" + fallbackName + "'.");
                    item.NameFallbackUsed = true;
                    item.Username = fallbackName;
                    item.Content = fallbackContent;
                    return new SendOutcome { RetryDelayMs = 0 };
                }
                // No HTTP answer at all (timeout, DNS failure, refused or dropped connection): a network blip, not a
                // rejection by Discord, so it is retried with the normal backoff instead of dropping the message.
                if (response == null && ex.Status != WebExceptionStatus.RequestCanceled)
                {
                    RelayDiagnostics.Warning("Discord webhook could not be reached (" + ex.Status + "): " + ex.Message);
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
