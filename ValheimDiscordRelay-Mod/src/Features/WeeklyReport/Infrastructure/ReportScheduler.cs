using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Decides when the weekly report is due, builds it and hands it to the webhook queue.
    /// Runs on the main thread, from the plugin Update(), and only does anything on the server.
    ///
    /// The report covers the period since the previous one (ReportStats.PeriodStartUtc). It is posted the first time the
    /// clock passes the configured weekday and time after that start; if the server was off at that moment, it is posted as
    /// soon as the server is running again.
    ///
    /// The statistics start over only after Discord has confirmed the report (a 2xx answer): if it is refused or cannot be
    /// delivered the week's numbers are kept and the report is tried again later (after DeliveryRetryDelay, up to
    /// MaxDeliveryAttempts times in all; then the period is closed with an error in the log, so a webhook that never works
    /// cannot make the statistics grow for ever). Without a webhook URL there is nothing to wait for: the period simply
    /// ends when the report is due. Activity that happens between the report being built and Discord confirming it
    /// (normally a few seconds) is not counted in either period.
    ///
    /// The moment Discord accepts the report, the webhook thread records that fact in the statistics file
    /// (ReportStats.MarkReportDelivered); the reset itself happens a moment later on the main thread. If the server stops in
    /// between, the next start sees the mark and begins a new period instead of posting the same report again. (A server
    /// that dies after Discord has posted the message but before its answer was read cannot know it was delivered, and
    /// would send it again; that cannot be avoided without a way to ask Discord.)
    /// </summary>
    internal static class ReportScheduler
    {
        private const float CheckIntervalSeconds = 30f;
        private const float NoWebhookWarningIntervalSeconds = 3600f;

        private static float _nextCheckTime;
        private static float _nextNoWebhookWarningTime;
        private static bool _wasDisabled;
        private static volatile bool _testRequested;

        private const int MaxDeliveryAttempts = 4;
        private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromMinutes(15);

        private const int DeliveryWaiting = 0;
        private const int DeliveryDelivered = 1;
        private const int DeliveryFailed = 2;

        // A report that has been handed to the webhook queue and is waiting for Discord's answer. The answer arrives on the
        // webhook thread (OnDeliveryComplete), which only sets _deliveryResult; Tick acts on it on the main thread.
        private static bool _delivering;
        private static volatile int _deliveryId;
        private static volatile int _deliveryResult;
        private static int _deliveryAttempts;
        private static DateTime _nextAttemptUtc = DateTime.MinValue;

        // The report is several Discord messages (one per tab, see ReportEmbedBuilder). They are kept between attempts, so a retry
        // after a partial failure only sends the messages Discord has not accepted yet, and nothing is posted twice.
        private static readonly object PartsSync = new object();
        private static List<string> _parts;
        private static bool[] _partDelivered;
        private static DateTime _partsPeriodStartUtc = DateTime.MinValue;

        /// <summary>The answers of the messages of one delivery attempt; the report is delivered when all of them are in and all were accepted.</summary>
        private sealed class DeliveryBatch
        {
            public int Remaining;
            public bool AllAccepted = true;
        }

        // ---------------------------------------------------------------- lifecycle

        /// <summary>
        /// Called once from the plugin Awake(), after ModSettings.Bind().
        /// </summary>
        internal static void Initialize()
        {
            ReportStats.Initialize();

            ReportConfig.SendTestReport.SettingChanged += OnTestSettingChanged;

            // "Send Test Report = true" left in the file also fires once at startup.
            if (ReportConfig.SendTestReport.Value)
                _testRequested = true;
        }

        /// <summary>
        /// Called once from the plugin OnDestroy().
        /// </summary>
        internal static void Shutdown()
        {
            if (ReportConfig.SendTestReport != null)
                ReportConfig.SendTestReport.SettingChanged -= OnTestSettingChanged;

            ReportStats.SaveIfDirty();
        }

        /// <summary>
        /// Called when the server is shutting down cleanly: make sure the week's numbers reach the disk.
        /// </summary>
        internal static void OnServerShutdown()
        {
            ReportStats.SaveIfDirty();
        }

        /// <summary>
        /// The setting can change on another thread (live config reload), so only a flag is set here; the work is done in Tick.
        /// </summary>
        private static void OnTestSettingChanged(object sender, EventArgs args)
        {
            if (ReportConfig.SendTestReport.Value)
                _testRequested = true;
        }

        // ---------------------------------------------------------------- per-frame

        internal static void Tick()
        {
            try
            {
                ZNet znet = ZNet.instance;

                // Send Test Report is synced, so a client can briefly see it turn true. Only the server acts on it: drop
                // the request here so it cannot fire later if this player ever hosts a world.
                if (znet != null && !znet.IsServer())
                {
                    _testRequested = false;
                    return;
                }

                if (znet == null || !ServerRelay.IsActive)
                    return;

                if (_testRequested)
                {
                    _testRequested = false;
                    ClearTestSetting();
                    Send(true);
                }

                float now = Time.unscaledTime;
                if (now < _nextCheckTime)
                    return;

                _nextCheckTime = now + CheckIntervalSeconds;

                ReportStats.SaveIfDirty();

                if (!ReportConfig.On)
                {
                    // Nothing is collected while off, so when it is switched on again the period must start fresh.
                    _wasDisabled = true;
                    return;
                }

                if (_wasDisabled)
                {
                    _wasDisabled = false;
                    ForgetDelivery();
                    ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
                    return;
                }

                if (_delivering)
                {
                    HandleDeliveryResult();
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                if (nowUtc >= NextDueUtc(ReportStats.PeriodStartUtc) && nowUtc >= _nextAttemptUtc)
                    Send(false);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Weekly report update failed.", ex);
            }
        }

        /// <summary>
        /// The first moment after <paramref name="periodStartUtc"/> that is the configured weekday at the configured time
        /// (server local time), as UTC.
        /// </summary>
        internal static DateTime NextDueUtc(DateTime periodStartUtc)
        {
            TimeSpan timeOfDay = ReportConfig.GetReportTime();
            DayOfWeek day = ReportConfig.ReportDay.Value;
            DateTime startLocal = periodStartUtc.ToLocalTime();

            for (int offset = 0; offset <= 14; offset++)
            {
                DateTime date = startLocal.Date.AddDays(offset);
                if (date.DayOfWeek != day)
                    continue;

                DateTime candidate = date + timeOfDay;
                if (candidate > startLocal)
                    return candidate.ToUniversalTime();
            }

            return periodStartUtc.AddDays(7);
        }

        // ---------------------------------------------------------------- delivery bookkeeping

        /// <summary>
        /// Runs on the webhook thread: only records the answer for the report that is waiting (an answer for a report that
        /// has since been forgotten is ignored).
        /// </summary>
        private static void OnDeliveryComplete(int deliveryId, bool delivered, DateTime periodStartUtc)
        {
            // First of all, before the main thread has a chance to act on the answer: write down that Discord has this
            // week's report, so a server that stops right now does not post it a second time after the restart. (Done
            // whatever the delivery id: Discord does have the report; it is ignored if that period is already over.)
            if (delivered)
            {
                try { ReportStats.MarkReportDelivered(periodStartUtc); }
                catch (Exception ex) { RelayDiagnostics.Warning("[Report] Could not record that the weekly report was delivered: " + ex.Message); }
            }

            if (deliveryId == _deliveryId)
                _deliveryResult = delivered ? DeliveryDelivered : DeliveryFailed;
        }

        private static void ForgetParts()
        {
            lock (PartsSync)
            {
                _parts = null;
                _partDelivered = null;
                _partsPeriodStartUtc = DateTime.MinValue;
            }
        }

        private static void ForgetDelivery()
        {
            ForgetParts();
            _deliveryId++;
            _deliveryResult = DeliveryWaiting;
            _delivering = false;
            _deliveryAttempts = 0;
            _nextAttemptUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Main thread: the report that was handed to the webhook queue has been answered.
        /// </summary>
        private static void HandleDeliveryResult()
        {
            int result = _deliveryResult;
            if (result == DeliveryWaiting)
                return;

            _delivering = false;
            _deliveryResult = DeliveryWaiting;

            if (result == DeliveryDelivered)
            {
                ForgetParts();
                _deliveryAttempts = 0;
                _nextAttemptUtc = DateTime.MinValue;
                RelayDiagnostics.Info("[Report] Weekly report delivered to Discord; starting a new statistics period.");
                ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
                return;
            }

            if (_deliveryAttempts >= MaxDeliveryAttempts)
            {
                ForgetParts();
                RelayDiagnostics.Error("[Report] The weekly report could not be delivered after " + _deliveryAttempts +
                                       " attempts; this period's statistics are discarded and a new period has started. " +
                                       "Check [Weekly Report] Server - Webhook URL.");
                _deliveryAttempts = 0;
                _nextAttemptUtc = DateTime.MinValue;
                ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
                return;
            }

            _nextAttemptUtc = DateTime.UtcNow + DeliveryRetryDelay;
            RelayDiagnostics.Warning("[Report] The weekly report was not delivered (attempt " + _deliveryAttempts + " of " +
                                     MaxDeliveryAttempts + "). The statistics are kept; trying again in " +
                                     (int)DeliveryRetryDelay.TotalMinutes + " minutes.");
        }

        // ---------------------------------------------------------------- sending

        private static void Send(bool isTest)
        {
            string url = ReportConfig.WebhookUrl.Value == null ? string.Empty : ReportConfig.WebhookUrl.Value.Trim();
            if (url.Length == 0)
            {
                if (!isTest)
                {
                    // Nothing can be posted, so there is nothing to wait for: the period ends when the report is due, like it
                    // would with a webhook. (Without this the statistics would grow for ever and the in-game window would
                    // say "any moment now" for good.)
                    RelayDiagnostics.Warning("[Report] The weekly report is due, but [Weekly Report] Server - Webhook URL is empty; " +
                                             "nothing was sent, and a new statistics period has started.");
                    ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
                    return;
                }

                // Hourly at most, so a missing webhook does not flood the log.
                float now = Time.unscaledTime;
                if (now >= _nextNoWebhookWarningTime)
                {
                    _nextNoWebhookWarningTime = now + NoWebhookWarningIntervalSeconds;
                    RelayDiagnostics.Warning("[Report] Test report is ready, but [Weekly Report] Server - Webhook URL is empty; nothing was sent.");
                }

                return;
            }

            ReportSnapshot snapshot = ReportStats.Snapshot();
            string serverName = ServerRelay.GetServerDisplayName();

            if (isTest)
            {
                List<string> testMessages = ReportEmbedBuilder.BuildMessages(snapshot, serverName, true);
                LogReportSummary(snapshot, true, testMessages.Count);

                for (int i = 0; i < testMessages.Count; i++)
                    ServerRelay.EnqueueJson(url, testMessages[i], "weekly report (test, message " + (i + 1) + " of " + testMessages.Count + ")",
                        exemptFromLimit: true);

                return;
            }

            // A retry after a partial delivery sends only what Discord has not accepted yet, from the messages built the first time
            // (a fresh build could split the report differently, and the numbers may have moved on). When nothing was accepted yet,
            // the report is simply built again from the current numbers.
            List<string> messages;
            bool[] delivered;
            lock (PartsSync)
            {
                bool reuse = _parts != null && _partDelivered != null && _partsPeriodStartUtc == snapshot.StartUtc && AnyTrue(_partDelivered);
                if (!reuse)
                {
                    _parts = ReportEmbedBuilder.BuildMessages(snapshot, serverName, false);
                    _partDelivered = new bool[_parts.Count];
                    _partsPeriodStartUtc = snapshot.StartUtc;
                }

                messages = _parts;
                delivered = _partDelivered;
            }

            LogReportSummary(snapshot, false, messages.Count);

            int toSend = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (!delivered[i])
                    toSend++;
            }

            // Set before queueing: the webhook thread may answer before EnqueueJson returns.
            int deliveryId = ++_deliveryId;
            _deliveryResult = DeliveryWaiting;
            _delivering = true;
            _deliveryAttempts++;

            DateTime periodStartUtc = snapshot.StartUtc;
            DeliveryBatch batch = new DeliveryBatch { Remaining = toSend };
            bool queuedAny = false;

            if (toSend == 0)
            {
                // Discord already has every message (the last answer was lost on the way): nothing is left to wait for.
                OnDeliveryComplete(deliveryId, true, periodStartUtc);
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                if (delivered[i])
                    continue;

                int index = i;
                bool queued = ServerRelay.EnqueueJson(url, messages[i],
                    "weekly report (message " + (i + 1) + " of " + messages.Count + ")",
                    delegate (bool accepted) { OnPartComplete(batch, delivered, index, accepted, deliveryId, periodStartUtc); },
                    exemptFromLimit: true);

                if (queued)
                {
                    queuedAny = true;
                    continue;
                }

                if (!queuedAny)
                {
                    // Not even queued (the webhook worker is not running): keep the statistics and look again in a while.
                    _delivering = false;
                    _deliveryAttempts--;
                    _nextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                    return;
                }

                // A later message could not be queued: it counts as not accepted, and the report is retried for it.
                OnPartComplete(batch, delivered, index, false, deliveryId, periodStartUtc);
            }
        }

        private static bool AnyTrue(bool[] values)
        {
            foreach (bool value in values)
            {
                if (value)
                    return true;
            }

            return false;
        }

        private static void LogReportSummary(ReportSnapshot snapshot, bool isTest, int messageCount)
        {
            RelayDiagnostics.Info("[Report] " + (isTest ? "Test report" : "Weekly report") + ": " +
                                  snapshot.JoinedCount + " player(s) joined, " +
                                  snapshot.TotalMobKills + " mob kill(s), " +
                                  snapshot.TotalDeaths + " death(s), " +
                                  snapshot.BossKills.Count + " boss kill(s), " +
                                  messageCount + " Discord message(s), period " +
                                  snapshot.StartUtc.ToString("u", CultureInfo.InvariantCulture) + " to " +
                                  snapshot.EndUtc.ToString("u", CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>
        /// Runs on the webhook thread: one message of the report was answered. When the last answer is in, the report counts as
        /// delivered only if Discord accepted every message.
        /// </summary>
        private static void OnPartComplete(DeliveryBatch batch, bool[] delivered, int index, bool accepted, int deliveryId, DateTime periodStartUtc)
        {
            bool finished;
            bool allAccepted;

            lock (PartsSync)
            {
                if (accepted && index < delivered.Length)
                    delivered[index] = true;

                if (!accepted)
                    batch.AllAccepted = false;

                batch.Remaining--;
                finished = batch.Remaining <= 0;
                allAccepted = batch.AllAccepted;
            }

            if (finished)
                OnDeliveryComplete(deliveryId, allAccepted, periodStartUtc);
        }

        private static void ClearTestSetting()
        {
            try
            {
                if (ReportConfig.SendTestReport.Value)
                    ReportConfig.SendTestReport.Value = false;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("[Report] Could not reset 'Send Test Report': " + ex.Message);
            }
        }
    }
}
