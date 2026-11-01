using System;
using BepInEx.Configuration;
using KydlCore;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The "Screenshots" tab of this mod in the KY hub (F6): one button per kind of screenshot. A button closes the hub, waits for it
    /// to disappear and then takes the screenshot exactly like its key does.
    /// </summary>
    internal static class ScreenshotHubPage
    {
        private const string SettingsTabId = "settings";
        private const string ScreenshotsTabId = "screenshots";

        /// <summary>How long after closing the hub the capture starts, so the window is not in the picture.</summary>
        private const float HubCloseDelaySeconds = 0.5f;

        /// <summary>
        /// Registers the tabs. A mod that registers any tab loses KydlCore's automatic Settings tab, so the Settings tab (the same
        /// page) is registered here too, first.
        /// </summary>
        internal static void Register(string modGuid, ConfigFile config, ClientCaptureService service)
        {
            KyHub.AddTab(modGuid, SettingsTabId, "Settings", () => KyHub.ConfigEntries(config), 0);
            KyHub.AddPage(modGuid, ScreenshotsTabId, "Screenshots", () => Build(service), 1);
            RelayDiagnostics.Debug("[Screenshot] Registered the Settings and Screenshots tabs in the KY hub.");
        }

        private static KyPage Build(ClientCaptureService service)
        {
            bool active = ClientConfig.ScreenshotsActive;
            bool hasWebhook = !string.IsNullOrWhiteSpace(ClientConfig.ScreenshotWebhookUrl.Value);
            string normalKey = KeyText(ClientConfig.ScreenshotKey.Value);
            string hudKey = KeyText(ClientConfig.ScreenshotHudHiddenKey.Value);
            string bothKey = KeyText(ClientConfig.ScreenshotHudPlayerHiddenKey.Value);

            KyPage page = new KyPage { State = active + "|" + hasWebhook + "|" + normalKey + "|" + hudKey + "|" + bothKey };

            KySection status = page.Section("Screenshots");
            status.Value("Screenshots", active ? "On" : "Off");
            status.Value("Discord webhook", hasWebhook ? "Set" : "Not set");

            if (!active || !hasWebhook)
            {
                status.Note(!active
                    ? "Screenshots are switched off: the server's [Screenshots] Enable or your own [Screenshots] Client - Enabled is off."
                    : "The server has no [Screenshots] Server - Webhook URL, so there is nowhere to send a screenshot.");
                return page;
            }

            KySection buttons = page.Section("Take a screenshot now");
            buttons.Note("Each button closes this window and sends the screenshot to Discord, the same as pressing its key.");
            buttons.Button("Screenshot with HUD (" + normalKey + ")", () => Press(service, ScreenshotMode.Normal));
            buttons.Button("Screenshot without HUD (" + hudKey + ")", () => Press(service, ScreenshotMode.HudHidden));
            buttons.Button("Screenshot without HUD and without your character (" + bothKey + ")", () => Press(service, ScreenshotMode.HudAndPlayerHidden));
            return page;
        }

        /// <summary>What a button does: closes the hub, then takes the screenshot after a short delay. Also called by the TestLab hook.</summary>
        internal static void Press(ClientCaptureService service, ScreenshotMode mode)
        {
            try
            {
                KyHub.Close();
                service.RequestScreenshot(mode, HubCloseDelaySeconds);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Screenshot button (" + mode + ") failed.", ex);
            }
        }

        private static string KeyText(KeyboardShortcut shortcut)
        {
            return shortcut.MainKey == UnityEngine.KeyCode.None ? "no key" : shortcut.ToString();
        }
    }
}
