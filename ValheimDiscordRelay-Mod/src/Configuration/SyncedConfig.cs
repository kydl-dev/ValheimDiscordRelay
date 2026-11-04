using BepInEx.Configuration;
using ServerSync;

namespace ValheimDiscordRelay;

// ServerSync wiring for the settings that are synced from the server (every "Server - ..." setting the players need).
//
// ServerSync mirrors the server's values to every connected client, and lets a
// change made in-game (Configuration Manager, F1) flow back to the server, which
// saves it to its own ValheimDiscordRelay.cfg and re-broadcasts it.
//
// Only the "Server - ..." settings go through this class. The "Client - ..." settings are plain
// ModSettings.File.Bind entries: each player keeps their own and ServerSync never touches them.
internal static class SyncedConfig
{
    // Identifies this mod's sync channel on the wire (RPC name + version check).
    // Never change it after release: old and new builds would stop talking.
    internal const string SyncName = ValheimDiscordRelayPlugin.ModGuid;

    internal static readonly ConfigSync Sync = new(SyncName)
    {
        DisplayName = "Valheim Discord Relay",

        // The version players and server must share (see AdminConfig.RequireSameVersion).
        // MinimumRequiredVersion is intentionally left unset: the check is "exactly this version", not "at least".
        CurrentVersion = ValheimDiscordRelayPlugin.ModVersion,

        // Must stay false: with true, a player who HAS this mod could not join any server that does not have it.
        // The server decides instead, with RequireExactVersion (switched on by Server.VersionEnforcement when this
        // game is a server and [Admin] Server - Require Same Version is on).
        ModRequired = false,

        // Every game tells the server its version when it connects, even though ModRequired is false. Without it the
        // server could not tell a player who has the mod from one who has not.
        AnnounceVersion = true,

        RequireExactVersion = false
    };

    // Binds a setting in the shared config file AND registers it with ServerSync.
    // Everything bound through here is sent to every connected player, so never use it
    // for webhook URLs only the server uses - use ModSettings.File.Bind for those. (Webhook URLs the clients
    // upload to are bound here on purpose: the admin sets them once on the server and every client receives them.)
    internal static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description)
    {
        ConfigEntry<T> entry = ModSettings.File.Bind(section, key, defaultValue, description);
        Sync.AddConfigEntry(entry);
        return entry;
    }

    internal static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        return Bind(section, key, defaultValue, new ConfigDescription(description));
    }

    // Like Bind, but this setting controls whether the synced settings are admin-only.
    // ServerSync allows exactly one such entry.
    internal static ConfigEntry<bool> BindLock(string section, string key, bool defaultValue, string description)
    {
        ConfigEntry<bool> entry = ModSettings.File.Bind(section, key, defaultValue, new ConfigDescription(description));
        Sync.AddLockingConfigEntry(entry);
        return entry;
    }
}
