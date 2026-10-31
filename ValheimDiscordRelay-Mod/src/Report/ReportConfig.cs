using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Report
{
    /// <summary>The two looks of the in-game report window (see ReportDefaultStyle and ReportModernStyle).</summary>
    internal enum ReportWindowStyle
    {
        /// <summary>The original flat look: square corners, gold and brown, the game's default UI font.</summary>
        Classic = 0,

        /// <summary>Rounded cards, pill tabs, a gold primary button, key hints in the footer and Valheim's own fonts.</summary>
        Modern = 1,
    }

    /// <summary>
    /// All [Weekly Report] settings, bound once into these fields by Init().
    ///
    /// "Server - ..." settings:
    ///   Enabled (synced). Clients need it so they stop collecting and reporting kills / damage when the report is off.
    ///   Report Day, Report Time, Send Test Report (synced; only admins can change or trigger them while Lock Configuration is on).
    ///   Window Enabled (synced). Lets players open the in-game report window (ReportWindow). Clients need it to know whether to try.
    ///   Webhook URL (local to the server, secret - never synced; only the server uses it).
    /// "Client - ..." settings, local to each player's game:
    ///   Enabled (this player's kills, damage and deaths are collected and reported).
    ///   Window Key, Window Scale (the in-game report window: which key opens it, and how large it is drawn).
    ///   Window Style (Classic or Modern look of that window; applied the next time it opens).
    ///   Window Width, Window Height (the size the player gave the window by dragging its corner; saved by the window itself).
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
        internal static ConfigEntry<bool> WindowEnabled;
        internal static ConfigEntry<KeyboardShortcut> WindowKey;
        internal static ConfigEntry<float> WindowScale;
        internal static ConfigEntry<ReportWindowStyle> WindowStyle;
        internal static ConfigEntry<float> WindowWidth;
        internal static ConfigEntry<float> WindowHeight;

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

            WindowEnabled = SyncedConfig.Bind(
                Section, "Server - Window Enabled", true,
                "Let players open the in-game weekly report window, which shows the same numbers as the Discord report as they stand " +
                "right now. Synced from the server: turn off to disable the window for every player. It has no effect on the Discord report " +
                "or on what is collected. Only admins can change it while [Admin] Server - Lock Configuration is on.");

            ClientEnabled = SharedConfig.File.Bind(
                Section, "Client - Enabled", true,
                "Include this player in the weekly report: this game measures the kills and damage you deal and reports your deaths to the server. " +
                "Turn off to opt out. (The server must also have [Weekly Report] Server - Enabled on.)");

            WindowKey = SharedConfig.File.Bind(
                Section, "Client - Window Key", new KeyboardShortcut(KeyCode.Home),
                "Key, or two keys pressed together, that open and close the in-game weekly report window. " +
                "Examples: Home, or F8 + LeftControl (hold both; the order you press them in does not matter). " +
                "Set to None to disable the hotkey. Escape also closes the window.");

            WindowScale = SharedConfig.File.Bind(
                Section, "Client - Window Scale", 0f,
                new ConfigDescription(
                    "Size of the in-game weekly report window. 0 = automatic (follows your screen height: normal size at 1080p, " +
                    "larger on taller screens). Otherwise 0.7 to 2.0, where 1.0 is the normal size.",
                    new AcceptableValueRange<float>(0f, 2f)));

            WindowStyle = SharedConfig.File.Bind(
                Section, "Client - Window Style", ReportWindowStyle.Classic,
                "Look of the in-game weekly report window. Classic = the original flat look. Modern = rounded cards, pill-shaped tabs, " +
                "a gold primary button, key hints in the footer and Valheim's own fonts. Only the look changes: the same numbers, tabs, " +
                "keys and window size. It applies the next time the window opens. Each style has its own style sheet in the config folder " +
                "(ValheimDiscordRelay.report.uss for Classic, ValheimDiscordRelay.report.modern.uss for Modern) that you can edit.");

            WindowWidth = SharedConfig.File.Bind(
                Section, "Client - Window Width", 0f,
                new ConfigDescription(
                    "Width of the in-game weekly report window, remembered between sessions. It is saved automatically when you drag the " +
                    "window's bottom-right corner, so there is normally no need to edit it. 0 = the default size. The window is never " +
                    "made narrower than its content needs. The unit is the window's own size before Client - Window Scale is applied.",
                    new AcceptableValueRange<float>(0f, 4000f)));

            WindowHeight = SharedConfig.File.Bind(
                Section, "Client - Window Height", 0f,
                new ConfigDescription(
                    "Height of the in-game weekly report window, remembered between sessions. It is saved automatically when you drag the " +
                    "window's bottom-right corner, so there is normally no need to edit it. 0 = the default size. The window is never " +
                    "made lower than its content needs. The unit is the window's own size before Client - Window Scale is applied.",
                    new AcceptableValueRange<float>(0f, 4000f)));
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
