using System;
using BepInEx.Configuration;

namespace ValheimDiscordRelay;

/// <summary>
/// All [Boss Death] settings, bound once into these fields by Init().
///
/// "Server - ..." settings, synced (admin-controlled through ServerSync, same lock as the [Chat] settings):
///   Enabled, Webhook URL, Webhook Username, Video Source.
/// "Client - ..." settings, local to each player's game:
///   Enabled (this player takes part in boss damage tracking and boss videos) and the video settings.
///
/// The server reads Webhook URL to post the embed; the client that owns the boss when it dies reads
/// the same value to upload the video. The admin sets it once on the server and every client receives it.
/// </summary>
internal static class BossConfig
{
    internal const string Section = "Boss Death";

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> ClientEnabled;
    internal static ConfigEntry<string> WebhookUrl;
    internal static ConfigEntry<string> WebhookUsername;
    internal static ConfigEntry<string> VideoSource;

    /// <summary>Boss announcements are on: the mod is on and [Boss Death] Enable is on.</summary>
    internal static bool On { get { return ModSettings.On && Enabled != null && Enabled.Value; } }

    /// <summary>This player takes part: <see cref="On"/> and [Boss Death] Client - Enabled is on.</summary>
    internal static bool ClientOn { get { return On && ClientEnabled.Value; } }

    /// <summary>Video Source value: the player whose game owns the boss when it dies records the video.</summary>
    internal const string VideoSourceOwner = "Boss Owner";

    /// <summary>Video Source value: the player who landed the last hit records the video.</summary>
    internal const string VideoSourceLastAttacker = "Last Attacker";

    internal static ConfigEntry<bool> VideoEnabled;
    internal static ConfigEntry<int> VideoFps;
    internal static ConfigEntry<string> VideoResolution;
    internal static ConfigEntry<int> VideoPreDuration;
    internal static ConfigEntry<int> VideoPostDuration;


    /// <summary>True when the admin chose "Last Attacker" as the boss video source.</summary>
    internal static bool UseLastAttackerVideo
    {
        get
        {
            return VideoSource != null &&
                   string.Equals(VideoSource.Value, VideoSourceLastAttacker, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void GetVideoResolution(out int width, out int height)
    {
        width = 960;
        height = 540;

        string value = VideoResolution.Value;
        if (value == "480x270") { width = 480; height = 270; }
        else if (value == "640x360") { width = 640; height = 360; }
    }
}
