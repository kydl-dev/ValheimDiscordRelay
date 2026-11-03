using BepInEx.Configuration;
using Kydl.Common;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Presentation.Hub.Registration
{
    /// <summary>What Valheim Discord Relay declares to the KY hub (F6): its title, the settings tab (every config entry) and the Screenshots tab.</summary>
    internal static class HubTabs
    {
        internal const string SettingsId = "settings";
        internal const string ScreenshotsId = "screenshots";

        /// <summary>Registers the mod page (a game client only; a dedicated server has no hub window).</summary>
        internal static bool RegisterModPage(ConfigFile config, ClientCaptureService service)
        {
            global::KydlCore.KyHub.SetModTitle(ValheimDiscordRelayPlugin.ModGuid, "Valheim Discord Relay");
            ScreenshotHubPage.Register(ValheimDiscordRelayPlugin.ModGuid, config, service);
            bool registered = global::KydlCore.KyHub.HasTab(ValheimDiscordRelayPlugin.ModGuid, SettingsId);
            ModLog.Verbose("HubTabs: settings tab " + (registered ? "registered" : "FAILED"));
            return registered;
        }

        internal static bool UnregisterModPage()
        {
            global::KydlCore.KyHub.RemoveTab(ValheimDiscordRelayPlugin.ModGuid, ScreenshotsId);
            global::KydlCore.KyHub.RemoveTab(ValheimDiscordRelayPlugin.ModGuid, SettingsId);
            return !global::KydlCore.KyHub.HasTab(ValheimDiscordRelayPlugin.ModGuid, SettingsId);
        }
    }
}
