using BepInEx.Configuration;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// All [Admin] settings, bound once into these fields by Init().
    ///
    /// Both plugins (server and client) end up here through SharedConfig.InitAll(), because the settings are read on
    /// both sides: the server posts chat / notifications / boss / report messages, and each player's game uploads
    /// screenshots and videos. Both plugins live in the same assembly, so the guard below makes sure everything is
    /// bound exactly once (ServerSync allows only one locking entry).
    ///
    /// Both settings are "Server - ..." settings: synced from the server and admin-controlled.
    /// [Admin] has no Enabled switch of its own: it is not a feature, it only controls the other features.
    /// </summary>
    internal static class AdminConfig
    {
        internal const string Section = "Admin";

        internal static ConfigEntry<bool> LockConfiguration;
        internal static ConfigEntry<bool> LogSuccessfulSends;

        private static bool _initialized;

        /// <summary>
        /// Binds every [Admin] setting. Safe to call more than once.
        /// </summary>
        internal static void Init()
        {
            if (_initialized)
                return;

            _initialized = true;

            LockConfiguration = SyncedConfig.BindLock(
                Section,
                "Server - Lock Configuration",
                true,
                "When true, only players listed in the server's adminlist.txt can change the synced settings from their game; everyone else's edits are ignored. Set to false to let ANY connected player change them (and have the server save it).");

            LogSuccessfulSends = SyncedConfig.Bind(
                Section,
                "Server - Log Successful Sends",
                false,
                "Log every successful Discord webhook delivery, from every category: chat, server notifications, player notifications, boss deaths and the weekly report (logged by the server), " +
                "and screenshot / death video / boss video uploads (logged in the log of the game that uploaded them).");
        }
    }
}
