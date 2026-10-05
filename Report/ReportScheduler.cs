using System;
using System.Globalization;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Decides when the weekly report is due, builds it and hands it to the webhook queue.
    /// Runs on the main thread, from ServerPlugin.Update(), and only does anything on the server.
    ///
    /// The report covers the period since the previous one (ReportStats.PeriodStartUtc). It is posted the first time the
    /// clock passes the configured weekday and time after that start; if the server was off at that moment, it is posted as
    /// soon as the server is running again. After a successful hand-off to the queue the statistics start over.
    /// </summary>
    internal static class ReportScheduler
    {
        private const float CheckIntervalSeconds = 30f;
        private const float NoWebhookWarningIntervalSeconds = 3600f;

        private static float _nextCheckTime;
        private static float _nextNoWebhookWarningTime;
        private static bool _wasDisabled;
        private static volatile bool _testRequested;

        // ---------------------------------------------------------------- lifecycle

        /// <summary>
        /// Called once from ServerPlugin.Awake(), after SharedConfig.InitAll().
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
        /// Called once from ServerPlugin.OnDestroy().
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

                if (!ReportConfig.Enabled.Value)
                {
                    // Nothing is collected while off, so when it is switched on again the period must start fresh.
                    _wasDisabled = true;
                    return;
                }

                if (_wasDisabled)
                {
                    _wasDisabled = false;
                    ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
                    return;
                }

                if (DateTime.UtcNow >= NextDueUtc(ReportStats.PeriodStartUtc))
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

        // ---------------------------------------------------------------- sending

        private static void Send(bool isTest)
        {
            string url = ReportConfig.WebhookUrl.Value == null ? string.Empty : ReportConfig.WebhookUrl.Value.Trim();
            if (url.Length == 0)
            {
                // Hourly at most, so a missing webhook does not flood the log.
                float now = Time.unscaledTime;
                if (now >= _nextNoWebhookWarningTime)
                {
                    _nextNoWebhookWarningTime = now + NoWebhookWarningIntervalSeconds;
                    RelayDiagnostics.Warning("[Report] " + (isTest ? "Test report" : "Weekly report") +
                                             " is ready, but [Weekly Report] Server - Webhook URL is empty; nothing was sent.");
                }

                return;
            }

            ReportSnapshot snapshot = ReportStats.Snapshot();
            string json = ReportEmbedBuilder.Build(snapshot, ServerRelay.GetServerDisplayName(), isTest);

            RelayDiagnostics.Info("[Report] " + (isTest ? "Test report" : "Weekly report") + ": " +
                                  snapshot.JoinedCount + " player(s) joined, " +
                                  snapshot.TotalMobKills + " mob kill(s), " +
                                  snapshot.TotalDeaths + " death(s), " +
                                  snapshot.BossKills.Count + " boss kill(s), period " +
                                  snapshot.StartUtc.ToString("u", CultureInfo.InvariantCulture) + " to " +
                                  snapshot.EndUtc.ToString("u", CultureInfo.InvariantCulture) + ".");

            bool queued = ServerRelay.EnqueueJson(url, json, isTest ? "weekly report (test)" : "weekly report");

            if (!isTest && queued)
                ReportStats.RestartPeriod(PlayerNotifications.GetOnlineNames());
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
