using BepInEx.Configuration;
using ServerSync;

namespace ValheimDiscordRelay
{
    // ServerSync wiring for the settings that are synced from the server (every "Server - ..." setting the players need).
    //
    // ServerSync mirrors the server's values to every connected client, and lets a
    // change made in-game (Configuration Manager, F1) flow back to the server, which
    // saves it to its own ValheimDiscordRelay.cfg and re-broadcasts it.
    //
    // Only the "Server - ..." settings go through this class. The "Client - ..." settings are plain
    // SharedConfig.File.Bind entries: each player keeps their own and ServerSync never touches them.
    internal static class SyncedConfig
    {
        // Identifies this mod's sync channel on the wire (RPC name + version check).
        // Never change it after release: old and new builds would stop talking.
        internal const string SyncName = "com.valheimdiscordrelay";

        internal static readonly ConfigSync Sync = new(SyncName)
        {
            DisplayName = "Valheim Discord Relay",

            // Only used to log a warning when the client and server builds differ.
            // MinimumRequiredVersion is intentionally left unset, so no build is ever rejected.
            CurrentVersion = Server.ServerPlugin.PluginVersion,

            // Must stay false: players who don't have this mod have to be able to join.
            // (true would disconnect anyone without the DLL.)
            ModRequired = false
        };

        // Binds a setting in the shared config file AND registers it with ServerSync.
        // Everything bound through here is sent to every connected player, so never use it
        // for webhook URLs only the server uses - use SharedConfig.File.Bind for those. (Webhook URLs the clients
        // upload to are bound here on purpose: the admin sets them once on the server and every client receives them.)
        internal static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description)
        {
            ConfigEntry<T> entry = SharedConfig.File.Bind(section, key, defaultValue, description);
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
            ConfigEntry<bool> entry = SharedConfig.File.Bind(section, key, defaultValue, new ConfigDescription(description));
            Sync.AddLockingConfigEntry(entry);
            return entry;
        }
    }
}
