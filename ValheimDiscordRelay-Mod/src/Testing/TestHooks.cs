using System.Linq;
using BepInEx.Bootstrap;
using Kydl.Common;

namespace ValheimDiscordRelay.Testing
{
    /// <summary>
    /// KydlCore D-084: hooks for DrakesTestLab suites (ValheimDiscordRelay-Tests/TestLab/suites/ValheimDiscordRelay). A hook is a static method with
    /// [TestHook("name")]; DrakesTestLab finds it by the attribute's NAME (the mod does not reference DrakesTestLab). Hook names carry the mod id.
    /// </summary>
    internal static class TestHooks
    {
        /// <summary>All Startup_* / World_* checks of InGameTests: "PASS: ..." or "FAIL: ...".</summary>
        [TestHook("ValheimDiscordRelay.selftest")]
        static string SelfTest() { return TestLabSupport.RunInGameTests(typeof(InGameTests)); }

        /// <summary>How many entries the mod's one config file holds (the KY hub shows all of them on the Settings tab).</summary>
        [TestHook("ValheimDiscordRelay.configEntries")]
        static int ConfigEntries()
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(ValheimDiscordRelayPlugin.ModGuid, out info) || info.Instance == null) return -1;
            return info.Instance.Config.Keys.Count();
        }

        /// <summary>Starts a screenshot of the given kind (0 normal, 1 without HUD, 2 without HUD and player) exactly like its key or F6 button; 1 when started.</summary>
        [TestHook("ValheimDiscordRelay.screenshot")]
        static int Screenshot(int mode)
        {
            if (ValheimDiscordRelayPlugin.CaptureService == null) return 0;
            ValheimDiscordRelayPlugin.CaptureService.RequestScreenshot((Client.ScreenshotMode)mode, 0f);
            return 1;
        }

        /// <summary>Presses the F6 hub's screenshot button of the given kind (same code as a click): the hub closes and the screenshot follows.</summary>
        [TestHook("ValheimDiscordRelay.hubButton")]
        static int HubButton(int mode)
        {
            if (ValheimDiscordRelayPlugin.CaptureService == null) return 0;
            Client.ScreenshotHubPage.Press(ValheimDiscordRelayPlugin.CaptureService, (Client.ScreenshotMode)mode);
            return 1;
        }

        /// <summary>1 while a screenshot is running, 0 when none is.</summary>
        [TestHook("ValheimDiscordRelay.screenshotRunning")]
        static int ScreenshotRunning()
        {
            return ValheimDiscordRelayPlugin.CaptureService != null && ValheimDiscordRelayPlugin.CaptureService.ScreenshotRunning ? 1 : 0;
        }

        /// <summary>1 while the HUD is hidden (Ctrl+F3 state), 0 when it is shown.</summary>
        [TestHook("ValheimDiscordRelay.hudHidden")]
        static int HudHidden() { return Hud.IsUserHidden() ? 1 : 0; }

        /// <summary>How many enabled renderers the local player's character has (0 while the player is hidden).</summary>
        [TestHook("ValheimDiscordRelay.playerRenderers")]
        static int PlayerRenderers()
        {
            if (Player.m_localPlayer == null) return -1;
            int count = 0;
            foreach (UnityEngine.Renderer renderer in Player.m_localPlayer.GetComponentsInChildren<UnityEngine.Renderer>(false))
                if (renderer.enabled) count++;
            return count;
        }

        /// <summary>1 when [General] EnableAll is on (the default), 0 when off, -1 when the setting is not bound.</summary>
        [TestHook("ValheimDiscordRelay.enabled")]
        static int Enabled()
        {
            return ModSettings.EnableAll == null ? -1 : (ModSettings.EnableAll.Value ? 1 : 0);
        }
    }
}
