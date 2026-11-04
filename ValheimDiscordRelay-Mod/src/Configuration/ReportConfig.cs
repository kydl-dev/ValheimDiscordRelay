using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Report;

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

    /// <summary>The weekly report is on: the mod is on and [Weekly Report] Enable is on.</summary>
    internal static bool On { get { return ModSettings.On && Enabled != null && Enabled.Value; } }

    /// <summary>This player is counted: <see cref="On"/> and [Weekly Report] Client - Enabled is on.</summary>
    internal static bool ClientOn { get { return On && ClientEnabled != null && ClientEnabled.Value; } }

    /// <summary>The in-game report window may open: <see cref="On"/> and [Weekly Report] Server - Window Enabled is on.</summary>
    internal static bool WindowOn { get { return On && WindowEnabled != null && WindowEnabled.Value; } }
    private static string _lastBadTimeWarned;


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
