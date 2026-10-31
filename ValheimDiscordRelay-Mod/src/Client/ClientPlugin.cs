using System;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;
using ValheimDiscordRelay.Report;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Plugin bootstrap: wires up config, the capture service and Harmony patches.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ClientPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.valheimdiscordrelay.client";
        public const string PluginName = "Valheim Discord Relay - Client";
        public const string PluginVersion = "1.3.5";

        /// <summary>
        /// Names what is special about this build, so a log can be matched to the code that produced it even when the
        /// version number has not changed between two test builds.
        /// </summary>
        public const string BuildTag = "hit-size-1";

        internal static ManualLogSource Log;
        internal static ClientCaptureService CaptureService;
        internal static bool SuppressChatRelay;

        private Harmony _harmony;

        /// <summary>
        /// "build &lt;tag&gt;, dll written &lt;UTC time&gt;" for the startup log lines.
        /// </summary>
        internal static string DescribeBuild()
        {
            string written;

            try
            {
                string location = typeof(ClientPlugin).Assembly.Location;
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
            Log = Logger;
            RelayDiagnostics.Initialize(Log, PluginName);

            SharedConfig.InitAll();

            CaptureService = new ClientCaptureService(this);
            CaptureService.Initialize();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(ChatSendTextPatch));
            _harmony.PatchAll(typeof(PlayerDamagePatch));
            _harmony.PatchAll(typeof(PlayerDeathStatePatch));
            _harmony.PatchAll(typeof(PlayerOnDeathPatch));
            _harmony.PatchAll(typeof(GameCameraDeathPatch));
            _harmony.PatchAll(typeof(GameSessionShutdownPatch));
            _harmony.PatchAll(typeof(BossDamagePatch));
            _harmony.PatchAll(typeof(BossOnDeathPatch));
            _harmony.PatchAll(typeof(BossVideoRpcRegistrationPatch));
            _harmony.PatchAll(typeof(ReportDamagePatch));
            _harmony.PatchAll(typeof(ReportCharacterDeathPatch));

            // The in-game weekly report window (UI Toolkit, created from code by a component on this plugin's GameObject). Its patches are
            // applied one at a time: they only matter while the window is open, so none of them may stop the plugin loading.
            ReportWindow.Create(gameObject);
            PatchOptional(typeof(ReportWindowRpcRegistrationPatch));
            PatchOptional(typeof(ReportWindowBlockControllerPatch));
            PatchOptional(typeof(ReportWindowBlockPlayerPatch));
            PatchOptional(typeof(ReportWindowCursorPatch));
            PatchOptional(typeof(ReportWindowEscapePatch));
            PatchOptional(typeof(ReportWindowScrollPatch));

            // Fires for the menu's Quit button, Alt+F4 and the window's close button alike.
            Application.quitting += HandleApplicationQuit;

            RelayDiagnostics.Info(PluginName + " " + PluginVersion + " loaded (" + DescribeBuild() + ").");
        }

        /// <summary>
        /// Applies one patch class. A patch that cannot be applied (for example after a game update renamed the method it
        /// targets) is logged and skipped; the rest of the mod keeps working.
        /// </summary>
        private void PatchOptional(Type patchClass)
        {
            try
            {
                _harmony.PatchAll(patchClass);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not apply the optional patch " + patchClass.Name + ".", ex);
            }
        }

        private void Update()
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsDedicated())
                    return;

                CaptureService.Tick();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Capture update failed.", ex);
            }

            try
            {
                BossKillTracker.Tick();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Boss damage report update failed.", ex);
            }

            try
            {
                ReportTracker.Tick();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Weekly report update failed.", ex);
            }
        }

        /// <summary>
        /// Posts a death message to in-game chat without it being relayed a second time to Discord.
        /// </summary>
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
                RelayDiagnostics.Error("Could not send death message to game chat.", ex);
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

        /// <summary>
        /// The game is quitting: stop any death/boss video that is still being processed and send what it was holding.
        /// </summary>
        private static void HandleApplicationQuit()
        {
            try
            {
                if (CaptureService != null)
                    CaptureService.OnApplicationQuitting();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process the game quit.", ex);
            }
        }

        private void OnDestroy()
        {
            Application.quitting -= HandleApplicationQuit;

            if (CaptureService != null)
                CaptureService.Dispose();

            if (_harmony != null)
                _harmony.UnpatchSelf();

            RelayDiagnostics.Shutdown();
        }
    }
}
