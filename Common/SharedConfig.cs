using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace ValheimDiscordRelay
{
    internal static class SharedConfig
    {
        internal const string FileName = "ValheimDiscordRelay.cfg";

        internal static readonly ConfigFile File = new(
            Path.Combine(Paths.ConfigPath, FileName),
            true);

        private static bool _initialized;

        /// <summary>
        /// Binds every setting of the mod, once, in the order the categories appear in the config file and in
        /// Configuration Manager. Both plugins (server and client) call this from Awake(); whichever runs first does
        /// the work, so the file layout never depends on which plugin BepInEx happens to load first.
        ///
        /// Naming convention for every key: "Server - ..." is set by the server admin (synced to players, or used only
        /// on the server), "Client - ..." is a per-player setting that lives in each player's own config.
        /// Inside a category the Server settings come first, then the Client settings.
        /// </summary>
        internal static void InitAll()
        {
            if (_initialized)
                return;

            _initialized = true;

            AdminConfig.Init();                                 // [Admin]
            Server.ServerConfig.InitChat();                     // [Chat]            Server - *
            Client.ClientConfig.InitChat();                     // [Chat]            Client - *
            Client.ClientConfig.InitScreenshots();              // [Screenshots]
            Client.ClientConfig.InitPlayerDeaths();             // [Player Deaths]
            BossConfig.Init();                                  // [Boss Death]
            Server.ServerConfig.InitServerNotifications();      // [Server Notifications]
            PlayerNotificationConfig.Init();                    // [Player Notifications]
            Report.ReportConfig.Init();                         // [Weekly Report]
        }
    }
}
