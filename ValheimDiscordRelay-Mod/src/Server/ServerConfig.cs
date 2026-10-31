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
        /// <summary>The chat relay is on: the mod is on and [Chat] Enable is on.</summary>
        internal static bool ChatOn { get { return ModSettings.On && Enabled.Value; } }
        /// <summary>Server notifications are on: the mod is on and [Server Notifications] Enable is on.</summary>
        internal static bool ServerNotificationsOn { get { return ModSettings.On && ServerNotificationsEnabled.Value; } }
        internal static ConfigEntry<string> ServerNotificationsWebhookUrl;
        internal static ConfigEntry<bool> UseDiscordWebhookName;
        internal static ConfigEntry<bool> ServerUpNotification;
        internal static ConfigEntry<bool> ServerDownNotification;
        internal static ConfigEntry<bool> ServerRestartNotification;
        internal static ConfigEntry<int> ServerRestartWindowSeconds;


    }
}
