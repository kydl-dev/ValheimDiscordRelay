using System;
using BepInEx.Configuration;

namespace ValheimDiscordRelay
{
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

        /// <summary>Video Source value: the player whose game owns the boss when it dies records the video.</summary>
        internal const string VideoSourceOwner = "Boss Owner";

        /// <summary>Video Source value: the player who landed the last hit records the video.</summary>
        internal const string VideoSourceLastAttacker = "Last Attacker";

        internal static ConfigEntry<bool> VideoEnabled;
        internal static ConfigEntry<int> VideoFps;
        internal static ConfigEntry<string> VideoResolution;
        internal static ConfigEntry<int> VideoPreDuration;
        internal static ConfigEntry<int> VideoPostDuration;

        private static bool _initialized;

        /// <summary>
        /// Binds every [Boss Death] setting. Safe to call more than once (server and client plugin both call it).
        /// </summary>
        internal static void Init()
        {
            if (_initialized)
                return;

            _initialized = true;

            Enabled = SyncedConfig.Bind(
                Section, "Server - Enabled", true,
                "Announce boss deaths in Discord (embed) and, optionally, with a short video. Synced from the server: turn off to disable the feature for every player.");

            WebhookUrl = SyncedConfig.Bind(
                Section, "Server - Webhook URL", "",
                "Discord webhook URL for boss death announcements. The server posts the embed to it; the player whose game " +
                "owns the boss when it dies uploads the video to it. Set by the server admin: the server's value is sent to every player, " +
                "and only admins can change it while [Admin] Server - Lock Configuration is on.");

            WebhookUsername = SyncedConfig.Bind(
                Section, "Server - Webhook Username", "Boss Death Announcement!",
                "Name shown as the sender of the embed and of the video. Synced from the server; only admins can change it while [Admin] Server - Lock Configuration is on.");

            VideoSource = SyncedConfig.Bind(
                Section, "Server - Video Source", VideoSourceOwner,
                new ConfigDescription(
                    "Whose game records the boss death video. \"Boss Owner\" (default): the player whose game owns the boss when it dies. " +
                    "\"Last Attacker\": the player who landed the last hit, who is almost certainly facing the boss. " +
                    "NOTE for admins: with \"Last Attacker\" the video is recorded by that player's own game, so THEIR client settings apply: " +
                    "[Player Deaths] Client - Video FPS and Client - Video Resolution (and [Player Deaths] Client - Video Enabled must be on), plus their own " +
                    "[Boss Death] Client - Enabled, Client - Video Enabled and Client - Video Pre/Post Duration. Your settings and the boss owner's settings are not used. " +
                    "If the last attacker cannot record (mod missing, video off, busy, no reply), the boss owner's video is used instead. " +
                    "Synced from the server; only admins can change it while [Admin] Server - Lock Configuration is on.",
                    new AcceptableValueList<string>(VideoSourceOwner, VideoSourceLastAttacker)));

            ClientEnabled = SharedConfig.File.Bind(
                Section, "Client - Enabled", true,
                "Take part in boss death announcements from this game: measure the damage you deal to bosses and record the boss death video when asked. " +
                "Turn off to opt out completely. (The server must also have [Boss Death] Server - Enabled on.)");

            VideoEnabled = SharedConfig.File.Bind(
                Section, "Client - Video Enabled", true,
                "Upload a short video of the moment the boss died. Only the client that records it (see Server - Video Source) needs this on.");

            VideoFps = SharedConfig.File.Bind(
                Section, "Client - Video FPS", 30,
                new ConfigDescription(
                    "Frames per second for the boss death video. Only used when [Player Deaths] Client - Video Enabled is off; otherwise the boss video reuses the death video frames and its FPS.",
                    new AcceptableValueList<int>(20, 30)));

            VideoResolution = SharedConfig.File.Bind(
                Section, "Client - Video Resolution", "960x540",
                new ConfigDescription(
                    "Boss death video resolution. Only used when [Player Deaths] Client - Video Enabled is off; otherwise the boss video reuses the death video frames and its resolution.",
                    new AcceptableValueList<string>("480x270", "640x360", "960x540")));

            VideoPreDuration = SharedConfig.File.Bind(
                Section, "Client - Video Pre Duration", 4,
                new ConfigDescription(
                    "Seconds captured before the boss dies. Allowed range is 2 to 4.",
                    new AcceptableValueRange<int>(2, 4)));

            VideoPostDuration = SharedConfig.File.Bind(
                Section, "Client - Video Post Duration", 4,
                new ConfigDescription(
                    "Seconds captured after the boss dies. Allowed range is 2 to 4.",
                    new AcceptableValueRange<int>(2, 4)));
        }

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
}
