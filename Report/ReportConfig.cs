using System;
using System.Globalization;
using BepInEx.Configuration;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// All [Weekly Report] settings, bound once into these fields by Init().
    ///
    /// "Server - ..." settings:
    ///   Enabled (synced). Clients need it so they stop collecting and reporting kills / damage when the report is off.
    ///   Report Day, Report Time, Send Test Report (synced; only admins can change or trigger them while Lock Configuration is on).
    ///   Webhook URL (local to the server, secret - never synced; only the server uses it).
    /// "Client - ..." settings, local to each player's game:
    ///   Enabled (this player's kills, damage and deaths are collected and reported).
    /// </summary>
    internal static class ReportConfig
    {
        internal const string Section = "Weekly Report";

        /// <summary>Name shown as the sender of the report.</summary>
        internal const string WebhookName = "Weekly report";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ClientEnabled;
        internal static ConfigEntry<string> WebhookUrl;
        internal static ConfigEntry<DayOfWeek> ReportDay;
        internal static ConfigEntry<string> ReportTime;
        internal static ConfigEntry<bool> SendTestReport;

        private static bool _initialized;
        private static string _lastBadTimeWarned;

        /// <summary>
        /// Binds every [Weekly Report] setting. Safe to call more than once (server and client plugin both call it).
        /// </summary>
        internal static void Init()
        {
            if (_initialized)
                return;

            _initialized = true;

            Enabled = SyncedConfig.Bind(
                Section, "Server - Enabled", true,
                "Collect weekly statistics (players joined, mob kills, deaths, boss kills, damage) and post them as one embed " +
                "once a week. Synced from the server: turn off to disable the feature for every player.");

            WebhookUrl = SharedConfig.File.Bind(
                Section, "Server - Webhook URL", "",
                "Discord webhook URL for the weekly report. Messages are sent as \"" + WebhookName + "\". Server-side only, never synced.");

            ReportDay = SyncedConfig.Bind(
                Section, "Server - Report Day", DayOfWeek.Sunday,
                "Day of the week the report is posted. Synced from the server; only admins can change it while [Admin] Server - Lock Configuration is on.");

            ReportTime = SyncedConfig.Bind(
                Section, "Server - Report Time", "20:00",
                "Time of day the report is posted, 24-hour HH:mm, in the SERVER's local time zone. Synced from the server; only admins can change it while [Admin] Server - Lock Configuration is on.");

            SendTestReport = SyncedConfig.Bind(
                Section, "Server - Send Test Report", false,
                "Debug helper. Set to true to post the report as it stands right now to the webhook, marked as a test. " +
                "Once set to true, the report is being sent and this option gets set to false instantly." +
                "It does not reset the statistics, and the setting turns itself back to false. " +
                "It fires when the setting changes (an admin in Configuration Manager, or a live config reload on the server) or at the next server start. " +
                "Synced from the server; only admins can trigger it while [Admin] Server - Lock Configuration is on.");

            ClientEnabled = SharedConfig.File.Bind(
                Section, "Client - Enabled", true,
                "Include this player in the weekly report: this game measures the kills and damage you deal and reports your deaths to the server. " +
                "Turn off to opt out. (The server must also have [Weekly Report] Server - Enabled on.)");
        }

        /// <summary>
        /// The configured time of day for the report. Falls back to 20:00 (with one warning) if the text is not HH:mm.
        /// </summary>
        internal static TimeSpan GetReportTime()
        {
            string text = ReportTime == null || ReportTime.Value == null ? string.Empty : ReportTime.Value.Trim();

            DateTime parsed;
            if (DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return parsed.TimeOfDay;

            if (!string.Equals(_lastBadTimeWarned, text, StringComparison.Ordinal))
            {
                _lastBadTimeWarned = text;
                RelayDiagnostics.Warning("[Report] [Weekly Report] Server - Report Time '" + text + "' is not in HH:mm format; using 20:00.");
            }

            return new TimeSpan(20, 0, 0);
        }
    }
}
