using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The settings that are read by the player's game, bound once into these fields:
    /// the "Client - ..." settings of [Chat], and all of [Screenshots] and [Player Deaths].
    ///
    /// Key naming: "Server - ..." entries are set by the admin on the server (bound through SyncedConfig, so every
    /// player receives the server's value); "Client - ..." entries are each player's own setting.
    ///
    /// There is no single master switch any more. Every category has its own "Server - Enabled" (admin, for everyone)
    /// and "Client - Enabled" (the player, for their own game); a feature runs only when both are on.
    /// </summary>
    internal static class ClientConfig
    {
        internal const string ChatSection = "Chat";
        internal const string ScreenshotsSection = "Screenshots";
        internal const string PlayerDeathsSection = "Player Deaths";

        // [Chat]
        internal static ConfigEntry<bool> ChatEnabled;
        internal static ConfigEntry<int> ChatMaxMessageLength;

        // [Screenshots]
        internal static ConfigEntry<bool> ScreenshotServerEnabled;
        internal static ConfigEntry<string> ScreenshotWebhookUrl;
        internal static ConfigEntry<bool> ScreenshotEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotKey;
        internal static ConfigEntry<bool> IncludeScreenshotPlayerName;

        // [Player Deaths]
        internal static ConfigEntry<bool> DeathServerEnabled;
        internal static ConfigEntry<string> DeathVideoWebhookUrl;
        internal static ConfigEntry<bool> DeathEnabled;
        internal static ConfigEntry<bool> DeathVideoEnabled;
        internal static ConfigEntry<int> DeathVideoFps;
        internal static ConfigEntry<string> DeathVideoResolution;
        internal static ConfigEntry<int> DeathVideoPreDuration;
        internal static ConfigEntry<int> DeathVideoPostDuration;

        /// <summary>True when manual screenshots are on for this player (admin and player switches both on).</summary>
        internal static bool ScreenshotsActive
        {
            get { return ScreenshotServerEnabled.Value && ScreenshotEnabled.Value; }
        }

        /// <summary>True when the in-game death shout and the Discord death message are on for this player.</summary>
        internal static bool DeathsActive
        {
            get { return DeathServerEnabled.Value && DeathEnabled.Value; }
        }

        /// <summary>
        /// True when this game records death videos, i.e. keeps the rolling frame buffer running.
        /// The boss death video reuses those frames while this is true.
        /// </summary>
        internal static bool DeathVideoActive
        {
            get { return DeathsActive && DeathVideoEnabled.Value; }
        }

        /// <summary>
        /// Binds the "Client - ..." settings of [Chat]. Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void InitChat()
        {
            ChatEnabled = SharedConfig.File.Bind(
                ChatSection, "Client - Enabled", true,
                "Send the chat you type in this game to the server so it can be relayed to Discord. Turn off to keep your own chat out of Discord. " +
                "(The server must also have [Chat] Server - Enabled on.)");

            ChatMaxMessageLength = SharedConfig.File.Bind(
                ChatSection, "Client - Max Message Length", 1000,
                new ConfigDescription(
                    "Maximum characters sent by the client. The server applies its own limit too.",
                    new AcceptableValueRange<int>(1, 1000)));
        }

        /// <summary>
        /// Binds every [Screenshots] setting. Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void InitScreenshots()
        {
            ScreenshotServerEnabled = SyncedConfig.Bind(
                ScreenshotsSection, "Server - Enabled", true,
                "Allow manual screenshots to be sent to Discord. Synced from the server: turn off to disable the feature for every player. " +
                "Only admins can change it while [Admin] Server - Lock Configuration is on.");

            ScreenshotWebhookUrl = SyncedConfig.Bind(
                ScreenshotsSection, "Server - Webhook URL", "",
                "Discord webhook URL used by clients for manual screenshots. Set by the server admin: the server's value is sent to every " +
                "player, and only admins can change it while [Admin] Server - Lock Configuration is on.");

            ScreenshotEnabled = SharedConfig.File.Bind(
                ScreenshotsSection, "Client - Enabled", true,
                "Allow the screenshot hotkey in this game. Turn off if you never want to send screenshots. " +
                "(The server must also have [Screenshots] Server - Enabled on.)");

            ScreenshotKey = SharedConfig.File.Bind(
                ScreenshotsSection, "Client - Key", new KeyboardShortcut(KeyCode.PageUp),
                "Key used to capture and send the current rendered Valheim screen to Discord. Set to None to disable the hotkey.");

            IncludeScreenshotPlayerName = SharedConfig.File.Bind(
                ScreenshotsSection, "Client - Include Player Name", true,
                "Use the local Valheim character name as the Discord webhook username for manual screenshots.");
        }

        /// <summary>
        /// Binds every [Player Deaths] setting. Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void InitPlayerDeaths()
        {
            DeathServerEnabled = SyncedConfig.Bind(
                PlayerDeathsSection, "Server - Enabled", true,
                "Enable player death messages (in-game shout and Discord message, with a video when videos are on). Synced from the server: " +
                "turn off to disable the feature for every player. Only admins can change it while [Admin] Server - Lock Configuration is on.");

            DeathVideoWebhookUrl = SyncedConfig.Bind(
                PlayerDeathsSection, "Server - Webhook URL", "",
                "Discord webhook URL used by clients for death videos and death messages. Set by the server admin: the server's value is sent " +
                "to every player, and only admins can change it while [Admin] Server - Lock Configuration is on. A dedicated webhook/channel is recommended.");

            DeathEnabled = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Enabled", true,
                "Announce this player's deaths: the in-game death shout and the Discord death message. Turn off to keep your deaths out of chat and Discord. " +
                "(The server must also have [Player Deaths] Server - Enabled on.)");

            DeathVideoEnabled = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Video Enabled", true,
                "Create a short WebP video around the local player's death. When off, the death message is still sent, without a video. " +
                "This also keeps the rolling frame buffer that boss death videos can reuse (see [Boss Death]).");

            DeathVideoFps = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Video FPS", 30,
                new ConfigDescription(
                    "Frames per second for death videos.",
                    new AcceptableValueList<int>(20, 30)));

            DeathVideoResolution = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Video Resolution", "960x540",
                new ConfigDescription(
                    "Death video resolution.",
                    new AcceptableValueList<string>("480x270", "640x360", "960x540")));

            DeathVideoPreDuration = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Video Pre Duration", 4,
                new ConfigDescription(
                    "Seconds captured before death. Allowed range is 2 to 4.",
                    new AcceptableValueRange<int>(2, 4)));

            DeathVideoPostDuration = SharedConfig.File.Bind(
                PlayerDeathsSection, "Client - Video Post Duration", 4,
                new ConfigDescription(
                    "Seconds captured after death. Allowed range is 2 to 4.",
                    new AcceptableValueRange<int>(2, 4)));
        }

        internal static bool TryGetDeathResolution(out int width, out int height)
        {
            width = 960;
            height = 540;

            string value = DeathVideoResolution.Value;
            if (value == "480x270") { width = 480; height = 270; return true; }
            if (value == "640x360") { width = 640; height = 360; return true; }
            if (value == "960x540") { width = 960; height = 540; return true; }

            RelayDiagnostics.Warning("Unknown [Player Deaths] Client - Video Resolution '" + value + "'; using 960x540.");
            return true;
        }
    }
}
