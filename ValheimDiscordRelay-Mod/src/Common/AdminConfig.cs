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
        internal static ConfigEntry<bool> RequireSameVersion;
        internal static ConfigEntry<bool> LogSuccessfulSends;

    }
}
