using System;
using BepInEx.Configuration;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// All [Player Notifications] settings, bound once into these fields by Init().
    ///
    /// Every setting here is a "Server - ..." setting (there is nothing to configure per player).
    /// Synced from the server (admin-controlled through ServerSync, same lock as the [Chat] settings):
    ///   Enabled, Arrival Avatar URL, Leave Avatar URL.
    /// Local to the server:
    ///   Webhook URL (secret - never synced).
    ///
    /// Enabled is synced because the CLIENT needs it: the client is the one that sees Valheim's own
    /// "I have arrived!" shout, and it must know whether to drop that shout (the mod posts its own join message instead)
    /// or relay it like any other shout (this feature is off).
    /// </summary>
    internal static class PlayerNotificationConfig
    {
        internal const string Section = "Player Notifications";

        /// <summary>Name shown as the sender of every join/leave message.</summary>
        internal const string WebhookName = "Player notifications";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> WebhookUrl;
        internal static ConfigEntry<string> ArrivalAvatarUrl;
        internal static ConfigEntry<string> LeaveAvatarUrl;

        private static bool _initialized;
        private static string _lastBadAvatarWarned;

        /// <summary>
        /// Binds every [Player Notifications] setting. Safe to call more than once (server and client plugin both call it).
        /// </summary>
        internal static void Init()
        {
            if (_initialized)
                return;

            _initialized = true;

            Enabled = SyncedConfig.Bind(
                Section, "Server - Enabled", true,
                "ON: Valheim's own arrival message (\"I have arrived!\") is intercepted and NOT sent to Discord. Instead the mod posts " +
                "\"<name> has joined the world. Population: <n>. Good luck!\" and \"<name> had to leave us. Hope they had fun. Population: <n>.\" " +
                "to this section's webhook. OFF: the game's arrival message is relayed like any other shout through the normal chat webhook, " +
                "and no join/leave messages are posted. Synced from the server.");

            WebhookUrl = SharedConfig.File.Bind(
                Section, "Server - Webhook URL", "",
                "Discord webhook URL for the join/leave messages. Messages are sent as \"" + WebhookName + "\". Server-side only, never synced.");

            ArrivalAvatarUrl = SyncedConfig.Bind(
                Section, "Server - Arrival Avatar URL", "",
                "Image URL (http/https) used as the webhook icon for join messages. Leave empty to use the webhook's default Discord icon. " +
                "Synced from the server; only admins can change it while Lock Configuration is on.");

            LeaveAvatarUrl = SyncedConfig.Bind(
                Section, "Server - Leave Avatar URL", "",
                "Image URL (http/https) used as the webhook icon for leave messages. Leave empty to use the webhook's default Discord icon. " +
                "Synced from the server; only admins can change it while Lock Configuration is on.");
        }

        /// <summary>
        /// Returns the avatar to send with a join or leave message, or null to let Discord use the webhook's own icon.
        /// Anything that is not an http/https URL is ignored (with one warning) rather than sent to Discord.
        /// </summary>
        /// <param name="arrival">True for a join message, false for a leave message.</param>
        internal static string GetAvatarUrl(bool arrival)
        {
            ConfigEntry<string> entry = arrival ? ArrivalAvatarUrl : LeaveAvatarUrl;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Value))
                return null;

            string value = entry.Value.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;

            if (!string.Equals(_lastBadAvatarWarned, value, StringComparison.Ordinal))
            {
                _lastBadAvatarWarned = value;
                RelayDiagnostics.Warning("[PlayerNotifications] " + entry.Definition.Key + " is not an http/https URL; using the webhook's default icon.");
            }

            return null;
        }
    }
}
