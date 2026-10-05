using BepInEx.Configuration;

namespace ValheimDiscordRelay.Server
{
    internal enum NameDisplayMode
    {
        NameOnly = 0,
        NameWithNumber = 1,
        NameWithIdSuffix = 2
    }

    /// <summary>
    /// The "Server - ..." settings of [Chat] and all of [Server Notifications], bound once into these fields.
    /// ([Chat] "Client - ..." lives in ClientConfig, [Player Notifications] in PlayerNotificationConfig,
    /// [Weekly Report] in Report/ReportConfig. SharedConfig.InitAll() calls everything in file order.)
    /// </summary>
    internal static class ServerConfig
    {
        internal const string ChatSection = "Chat";
        internal const string ServerNotificationsSection = "Server Notifications";

        /// <summary>Name shown as the sender of server up/down/restart messages (unless UseDiscordWebhookName is on).</summary>
        internal const string ServerNotificationsWebhookName = "Server notifications";

        // [Chat]
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> WebhookUrl;
        internal static ConfigEntry<string> NormalWebhookUrl;
        internal static ConfigEntry<string> ShoutWebhookUrl;
        internal static ConfigEntry<int> MaxMessageLength;
        internal static ConfigEntry<NameDisplayMode> NameDisplay;
        internal static ConfigEntry<string> ShoutAnsi;
        internal static ConfigEntry<string> NormalAnsi;
        internal static ConfigEntry<int> QueueLimit;
        internal static ConfigEntry<int> SendIntervalMs;

        // [Server Notifications]
        internal static ConfigEntry<bool> ServerNotificationsEnabled;
        internal static ConfigEntry<string> ServerNotificationsWebhookUrl;
        internal static ConfigEntry<bool> UseDiscordWebhookName;
        internal static ConfigEntry<bool> ServerUpNotification;
        internal static ConfigEntry<bool> ServerDownNotification;
        internal static ConfigEntry<bool> ServerRestartNotification;
        internal static ConfigEntry<int> ServerRestartWindowSeconds;

        /// <summary>
        /// Binds the "Server - ..." settings of [Chat]. Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void InitChat()
        {
            Enabled = SyncedConfig.Bind(
                ChatSection,
                "Server - Enabled",
                true,
                "Enable the Valheim chat -> Discord relay. Synced from the server. When off, nothing is posted to the chat webhooks.");

            WebhookUrl = SharedConfig.File.Bind(
                ChatSection,
                "Server - Webhook URL",
                "",
                "Fallback Discord webhook URL. Used only when the specific Normal/Shout webhook is empty. Server-side only, never synced.");

            NormalWebhookUrl = SharedConfig.File.Bind(
                ChatSection,
                "Server - Normal Webhook URL",
                "",
                "Discord webhook URL for normal chat. Server-side only, never synced.");

            ShoutWebhookUrl = SharedConfig.File.Bind(
                ChatSection,
                "Server - Shout Webhook URL",
                "",
                "Discord webhook URL for shouts. Server-side only, never synced.");

            MaxMessageLength = SyncedConfig.Bind(
                ChatSection,
                "Server - Max Message Length",
                1800,
                new ConfigDescription(
                    "Maximum message text sent to Discord. Discord content is additionally wrapped in an ANSI code block.",
                    new AcceptableValueRange<int>(100, 1900)));

            NameDisplay = SyncedConfig.Bind(
                ChatSection,
                "Server - Name Display",
                NameDisplayMode.NameOnly,
                "Discord webhook username format. NameOnly = Bjorn. NameWithNumber = Bjorn [1], Bjorn [2] for duplicate character names. NameWithIdSuffix = Bjorn [5678], using only the last 4 digits of the platform ID.");

            ShoutAnsi = SyncedConfig.Bind(
                ChatSection,
                "Server - Shout Prefix",
                "\u001b[2;31m",
                "ANSI escape sequence placed before a shout.");

            NormalAnsi = SyncedConfig.Bind(
                ChatSection,
                "Server - Normal Prefix",
                "\u001b[2;36m",
                "ANSI escape sequence placed before normal chat.");

            QueueLimit = SyncedConfig.Bind(
                ChatSection,
                "Server - Queue Limit",
                100,
                new ConfigDescription(
                    "Maximum queued Discord messages. Oldest messages are discarded if the queue is full.",
                    new AcceptableValueRange<int>(10, 1000)));

            SendIntervalMs = SyncedConfig.Bind(
                ChatSection,
                "Server - Minimum Send Interval Ms",
                250,
                new ConfigDescription(
                    "Minimum delay between webhook requests. This is intentionally paced to avoid webhook rate-limit bursts.",
                    new AcceptableValueRange<int>(50, 5000)));
        }

        /// <summary>
        /// Binds every [Server Notifications] setting (all "Server - ..."). Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void InitServerNotifications()
        {
            ServerNotificationsEnabled = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Enabled", true,
                "Enable server up/down/restart notifications in a dedicated Discord channel/webhook. (Player join/leave messages have their own [Player Notifications] section.)");

            ServerNotificationsWebhookUrl = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Webhook URL", "",
                "Discord webhook URL for server up/down/restart notifications. Server-side only, never synced.");

            UseDiscordWebhookName = SyncedConfig.Bind(
                ServerNotificationsSection, "Server - Use Discord Webhook Name", false,
                "Unchecked: messages are sent with the username \"" + ServerNotificationsWebhookName + "\". " +
                "Checked: no username is sent, so Discord shows the name that is set on the webhook itself (Channel settings > Integrations > Webhooks). " +
                "Synced from the server; only admins can change it while [Admin] Server - Lock Configuration is on.");

            ServerUpNotification = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Up Notification", true,
                "Send a notification when the Valheim server starts. A start within Server - Restart Window Seconds of a clean shutdown is reported as a restart instead.");

            ServerDownNotification = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Down Notification", true,
                "Send a notification when the Valheim server shuts down cleanly.");

            ServerRestartNotification = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Restart Notification", true,
                "Send a notification when the server starts shortly after a clean shutdown (see Server - Restart Window Seconds).");

            ServerRestartWindowSeconds = SharedConfig.File.Bind(
                ServerNotificationsSection, "Server - Restart Window Seconds", 120,
                new ConfigDescription(
                    "A startup counts as a restart only if it happens within this many seconds of the last clean shutdown. " +
                    "The time includes the server booting and loading the world. Later startups are reported as Server Up. " +
                    "Raise it if your server takes longer than this to come back during a real restart.",
                    new AcceptableValueRange<int>(10, 3600)));
        }
    }
}
