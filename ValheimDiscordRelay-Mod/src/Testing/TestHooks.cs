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
            return ValheimDiscordRelayPlugin.CaptureService.RequestScreenshot((Client.ScreenshotMode)mode, 0f) ? 1 : 0;
        }

        /// <summary>Presses the F6 hub's screenshot button of the given kind (same code as a click): the hub closes and the screenshot follows.</summary>
        [TestHook("ValheimDiscordRelay.hubButton")]
        static int HubButton(int mode)
        {
            if (ValheimDiscordRelayPlugin.CaptureService == null) return 0;
            Client.ScreenshotHubPage.Press(ValheimDiscordRelayPlugin.CaptureService, (Client.ScreenshotMode)mode);
            return 1;
        }

        /// <summary>How many screenshot buttons the F6 hub page shows right now.</summary>
        [TestHook("ValheimDiscordRelay.hubButtons")]
        static int HubButtons()
        {
            return ValheimDiscordRelayPlugin.CaptureService == null ? -1 : Client.ScreenshotHubPage.VisibleButtonCount(ValheimDiscordRelayPlugin.CaptureService);
        }

        /// <summary>Switches one kind of screenshot (0 normal, 1 without HUD, 2 without HUD and player) on (1) or off (0) in this game, like the player's own config switch.</summary>
        [TestHook("ValheimDiscordRelay.setKindEnabled")]
        static int SetKindEnabled(int mode, int on)
        {
            BepInEx.Configuration.ConfigEntry<bool> entry = mode == 1 ? Client.ClientConfig.ScreenshotHudHiddenEnabled
                : mode == 2 ? Client.ClientConfig.ScreenshotHudPlayerHiddenEnabled
                : Client.ClientConfig.ScreenshotNormalEnabled;
            entry.Value = on != 0;
            return entry.Value ? 1 : 0;
        }

        /// <summary>1 when this kind of screenshot can be taken now (on, and a webhook exists for it).</summary>
        [TestHook("ValheimDiscordRelay.kindActive")]
        static int KindActive(int mode) { return Client.ClientConfig.ScreenshotKindActive((Client.ScreenshotMode)mode) ? 1 : 0; }

        /// <summary>The webhook URL a screenshot of this kind would go to (own webhook, else the global one).</summary>
        [TestHook("ValheimDiscordRelay.webhookFor")]
        static string WebhookFor(int mode) { return Client.ClientConfig.ScreenshotWebhookFor((Client.ScreenshotMode)mode); }

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

        // ---- live Discord checks (suite test discord-live): the webhook comes from the environment variable VDR_TEST_WEBHOOK (never from a file in git)

        /// <summary>The test webhook from the environment, or null (the live test is then skipped).</summary>
        static string LiveWebhook()
        {
            string url = System.Environment.GetEnvironmentVariable("VDR_TEST_WEBHOOK");
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }

        /// <summary>
        /// Sends one chat line to the live test webhook through the real WebhookWorker (the server's sender), under the given player name.
        /// A name with "discord" in it is refused by Discord with HTTP 400 and must arrive anyway under the neutral name. Returns "ok sent" or "ok skipped (...)" when no webhook is set.
        /// </summary>
        [TestHook("ValheimDiscordRelay.liveChat")]
        static string LiveChat(string name)
        {
            string url = LiveWebhook();
            if (url == null) return "ok skipped (VDR_TEST_WEBHOOK is not set)";

            Server.WebhookWorker worker = new Server.WebhookWorker();
            worker.Start();
            worker.Enqueue(url, name, "[VDR test] chat line from player '" + name + "'");
            System.Threading.Thread.Sleep(1500);
            worker.Stop(15000);   // keeps sending what is queued, for at most 15 seconds
            return "ok sent";
        }

        /// <summary>
        /// Uploads a tiny WebP as a clip to the live test webhook with the real clip sender code. Returns "ok", "ok skipped (...)" when no webhook is set, or the error text.
        /// </summary>
        [TestHook("ValheimDiscordRelay.liveClip")]
        static string LiveClip()
        {
            string url = LiveWebhook();
            if (url == null) return "ok skipped (VDR_TEST_WEBHOOK is not set)";

            try
            {
                byte[] webp = System.Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==");
                Client.ClientCaptureService.SendClipFile(url, "vdr-test.webp", webp, "[VDR test] clip upload");
                return "ok";
            }
            catch (System.Exception ex)
            {
                return "error: " + ex.Message;
            }
        }
    }
}
