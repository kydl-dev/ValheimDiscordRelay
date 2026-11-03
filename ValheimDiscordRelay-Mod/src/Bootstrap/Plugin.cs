using System;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using Jotunn.Utils;
using KydlCore.Common;
using UnityEngine;
using ValheimDiscordRelay.Bootstrap;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// The ONE plugin of the mod: wires up the config, the server relay, the capture service in the player's game and the Harmony patches.
    /// What runs is decided at run time, not by two plugin classes: the server parts act only when this game is a server (dedicated or hosted),
    /// the capture parts only in a game with a screen. A hosted world runs both.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [BepInDependency(KydlCore.Bootstrap.Plugin.ModGuid, "0.1.0")]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Patch)]
    public sealed class ValheimDiscordRelayPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.kydldev.ValheimDiscordRelay";   // also the config file name: com.kydldev.ValheimDiscordRelay.cfg
        public const string ModName = "ValheimDiscordRelay";
        public const string ModVersion = "2.0.0";

        /// <summary>
        /// Names what is special about this build, so a log can be matched to the code that produced it even when the
        /// version number has not changed between two test builds.
        /// </summary>
        public const string BuildTag = "kydlcore-1";

        internal static ClientCaptureService CaptureService;
        internal static bool SuppressChatRelay;

        private Harmony _harmony;
        private ModInstaller _installer;
        private bool _refreshQueued;

        /// <summary>"build &lt;tag&gt;, dll written &lt;UTC time&gt;" for the startup log lines.</summary>
        internal static string DescribeBuild()
        {
            string written;

            try
            {
                string location = typeof(ValheimDiscordRelayPlugin).Assembly.Location;
                written = !string.IsNullOrEmpty(location) && File.Exists(location)
                    ? File.GetLastWriteTimeUtc(location).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : "unknown";
            }
            catch (Exception)
            {
                written = "unknown";
            }

            return "build " + BuildTag + ", dll written " + written;
        }

        private void Awake()
        {
            ModLog.Init(Logger, ModName, () => ModSettings.Verbose);
            ModSettings.Bind(Config);

            ReportScheduler.Initialize();
            ServerRelay.Initialize();
            BossAnnouncer.Initialize();

            CaptureService = new ClientCaptureService(this);
            CaptureService.Initialize();

            // The F6 hub page (settings + screenshots tabs) and the features; a dedicated server has no hub window.
            _installer = new ModInstaller(Config, CaptureService, !Application.isBatchMode);
            if (!_installer.Install())
                ModLog.Warn("ModInstaller did not finish; see the lines above.");
            Config.SettingChanged += (s, e) => _refreshQueued = true;

            _harmony = new Harmony(ModGuid);

            VersionEnforcement.Initialize();
            PatchInstaller.ApplyRequired(_harmony);

            // The in-game weekly report window (UI Toolkit, created from code by a component on this plugin's GameObject). Its patches are
            // applied one at a time: they only matter while the window is open, so none of them may stop the plugin loading.
            ReportWindow.Create(gameObject);
            PatchInstaller.ApplyOptional(_harmony);

            // Fires for the menu's Quit button, Alt+F4 and the window's close button alike.
            Application.quitting += HandleApplicationQuit;

            ModLog.Info(ModName + " " + ModVersion + " loaded (" + DescribeBuild() + "). EnableAll=" + ModSettings.EnableAll.Value);
            WarnAboutSharedWebhooks();
            ModLog.Info("ServerSync active (webhook URLs only the server uses are never synced to players). Config lock: " +
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
                var shared = new System.Collections.Generic.List<string>();
                AddIfSet(shared, ClientConfig.ScreenshotWebhookUrl, "Screenshots");
                AddIfSet(shared, ClientConfig.DeathVideoWebhookUrl, "Player Deaths");
                AddIfSet(shared, BossConfig.WebhookUrl, "Boss Death");
                AddIfSet(shared, ClipConfig.WebhookUrl, "Clips");

                if (shared.Count > 0)
                    ModLog.Info("Webhook URL(s) sent to every player's game (not secret, use dedicated channels): " + string.Join(", ", shared.ToArray()) + ".");
            }
            catch (Exception ex)
            {
                ModLog.Warn("Could not check the shared webhooks: " + ex.Message);
            }
        }

        private static void AddIfSet(System.Collections.Generic.List<string> names, BepInEx.Configuration.ConfigEntry<string> entry, string section)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Value))
                names.Add("[" + section + "]");
        }

        private void Update()
        {
            if (_refreshQueued)
            {
                _refreshQueued = false;
                if (_installer != null)
                    _installer.Refresh();
            }

            AdminSettingsChannel.Tick();   // works with the mod switched off too: an admin must be able to switch it back on from the game
            if (!ModSettings.On)
                return;

            // server side: both do nothing unless this game is the server, and throttle themselves
            PlayerNotifications.Tick();
            ReportScheduler.Tick();

            // player's game
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsDedicated())
                    return;

                CaptureService.Tick();
            }
            catch (Exception ex)
            {
                ModLog.Error("Capture update failed. " + ex);
            }

            try
            {
                BossKillTracker.Tick();
            }
            catch (Exception ex)
            {
                ModLog.Error("Boss damage report update failed. " + ex);
            }

            try
            {
                ReportTracker.Tick();
            }
            catch (Exception ex)
            {
                ModLog.Error("[Report] Weekly report update failed. " + ex);
            }
        }

        /// <summary>Posts a death message to in-game chat without it being relayed a second time to Discord.</summary>
        /// <param name="message">The death message to post.</param>
        internal static void SendDeathShout(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                if (Chat.instance == null)
                    return;

                SuppressChatRelay = true;
                Chat.instance.SendText(Talker.Type.Shout, message);
            }
            catch (Exception ex)
            {
                ModLog.Error("Could not send death message to game chat. " + ex);
            }
            finally
            {
                SuppressChatRelay = false;
            }
        }

        /// <summary>
        /// Unity message, sent to every MonoBehaviour when the application quits. Together with Application.quitting
        /// (the handler ignores the second call) so the quit is caught whichever fires first.
        /// </summary>
        private void OnApplicationQuit()
        {
            HandleApplicationQuit();
        }

        /// <summary>The game is quitting: stop any death/boss video that is still being processed and send what it was holding.</summary>
        private static void HandleApplicationQuit()
        {
            try
            {
                if (CaptureService != null)
                    CaptureService.OnApplicationQuitting();
            }
            catch (Exception ex)
            {
                ModLog.Error("Could not process the game quit. " + ex);
            }
        }

        private void OnDestroy()
        {
            Application.quitting -= HandleApplicationQuit;

            if (_installer != null)
                _installer.Uninstall();

            ReportScheduler.Shutdown();
            ServerRelay.Shutdown();

            if (CaptureService != null)
                CaptureService.Dispose();

            if (_harmony != null)
                _harmony.UnpatchSelf();
        }
    }
}
