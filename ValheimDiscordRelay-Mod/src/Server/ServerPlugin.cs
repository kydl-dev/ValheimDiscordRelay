using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using BepInEx.Logging;
using ValheimDiscordRelay.Report;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Plugin bootstrap: wires up config, the relay and Harmony patches.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ServerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.valheimdiscordrelay.server";
        public const string PluginName = "Valheim Discord Relay - Server";
        public const string PluginVersion = "1.3.5";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            RelayDiagnostics.Initialize(Log, PluginName);

            SharedConfig.InitAll();
            ExposeSharedConfigToConfigurationManager();

            ServerRelay.Initialize();
            ReportScheduler.Initialize();
            BossAnnouncer.Initialize();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(GameStartPatch));
            _harmony.PatchAll(typeof(GameShutdownPatch));
            _harmony.PatchAll(typeof(GameLogoutPatch));
            _harmony.PatchAll(typeof(ZNetDisconnectPatch));
            _harmony.PatchAll(typeof(ZNetShutdownPatch));
            VersionEnforcement.Initialize(_harmony);

            RelayDiagnostics.Info(PluginName + " " + PluginVersion + " loaded.");
            RelayDiagnostics.Info("Loaded. Server relay will activate only when running as a Valheim server.");
            WarnAboutSharedWebhooks();

            RelayDiagnostics.Info("ServerSync active (webhook URLs only the server uses are never synced). Config lock: " +
                                  (AdminConfig.LockConfiguration.Value ? "ON - admins only." : "OFF - any player can change synced settings."));
        }

        /// <summary>
        /// The webhooks the players' games upload to (screenshots, death videos, boss videos, clips) are sent to every player,
        /// because each game posts its own video straight to Discord. Say so once in the log, so the admin knows those
        /// webhooks are not secret and should not be one that can reach anything important.
        /// </summary>
        private static void WarnAboutSharedWebhooks()
        {
            try
            {
                System.Collections.Generic.List<string> shared = new System.Collections.Generic.List<string>();
                AddIfSet(shared, ValheimDiscordRelay.Client.ClientConfig.ScreenshotWebhookUrl, "Screenshots");
                AddIfSet(shared, ValheimDiscordRelay.Client.ClientConfig.DeathVideoWebhookUrl, "Player Deaths");
                AddIfSet(shared, ValheimDiscordRelay.BossConfig.WebhookUrl, "Boss Death");
                AddIfSet(shared, ValheimDiscordRelay.Client.ClipConfig.WebhookUrl, "Clips");

                if (shared.Count > 0)
                {
                    RelayDiagnostics.Info("Webhook URL(s) sent to every player's game (not secret, use dedicated channels): " +
                                          string.Join(", ", shared.ToArray()) + ".");
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not check the shared webhooks: " + ex.Message);
            }
        }

        private static void AddIfSet(System.Collections.Generic.List<string> names, BepInEx.Configuration.ConfigEntry<string> entry, string section)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Value))
                names.Add("[" + section + "]");
        }

        /// <summary>
        /// Points this plugin's Config at the shared ConfigFile so Configuration Manager (F1) lists it.
        /// </summary>
        private void ExposeSharedConfigToConfigurationManager()
        {
            try
            {
                FieldInfo field = typeof(BaseUnityPlugin).GetField(
                    "<Config>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (field == null)
                {
                    RelayDiagnostics.Warning("Could not expose settings to Configuration Manager (BepInEx field not found).");
                    return;
                }

                field.SetValue(this, SharedConfig.File);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not expose settings to Configuration Manager: " + ex.Message);
            }
        }

        /// <summary>
        /// Main-thread per-frame work. Both calls do nothing unless this game is the server, and throttle themselves.
        /// </summary>
        private void Update()
        {
            PlayerNotifications.Tick();
            ReportScheduler.Tick();
        }

        private void OnDestroy()
        {
            ReportScheduler.Shutdown();
            ServerRelay.Shutdown();

            if (_harmony != null)
                _harmony.UnpatchSelf();

            RelayDiagnostics.Shutdown();
        }
    }
}
