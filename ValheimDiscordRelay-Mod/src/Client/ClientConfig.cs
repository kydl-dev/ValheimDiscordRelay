using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The settings that are read by the player's game, bound once into these fields:
    /// the "Client - ..." settings of [Chat], and all of [Screenshots] and [Player Deaths].
    ///
    /// Key naming: "Server - ..." entries are set by the admin on the server (bound through SyncedConfig, so every
    /// player receives the server's value); "Client - ..." entries are each player's own setting.
    ///
    /// There is no single master switch any more. Every category has its own "Server - Enabled" (admin, for everyone)
    /// and "Client - Enabled" (the player, for their own game); a feature runs only when both are on.
    /// </summary>
    internal static class ClientConfig
    {
        internal const string ChatSection = "Chat";
        internal const string ScreenshotsSection = "Screenshots";
        internal const string PlayerDeathsSection = "Player Deaths";

        // [Chat]
        internal static ConfigEntry<bool> ChatEnabled;
        /// <summary>The player's chat goes to the server: the mod is on and [Chat] Client - Enabled is on.</summary>
        internal static bool ChatOn { get { return ModSettings.On && ChatEnabled.Value; } }
        internal static ConfigEntry<int> ChatMaxMessageLength;

        // [Screenshots]
        internal static ConfigEntry<bool> ScreenshotServerEnabled;
        internal static ConfigEntry<string> ScreenshotWebhookUrl;
        internal static ConfigEntry<bool> ScreenshotEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotKey;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotHudHiddenKey;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotHudPlayerHiddenKey;
        internal static ConfigEntry<bool> IncludeScreenshotPlayerName;

        // [Player Deaths]
        internal static ConfigEntry<bool> DeathServerEnabled;
        internal static ConfigEntry<string> DeathVideoWebhookUrl;
        internal static ConfigEntry<bool> DeathEnabled;
        internal static ConfigEntry<bool> DeathVideoEnabled;
        internal static ConfigEntry<int> DeathVideoFps;
        internal static ConfigEntry<string> DeathVideoResolution;
        internal static ConfigEntry<int> DeathVideoPreDuration;
        internal static ConfigEntry<int> DeathVideoPostDuration;
        internal static ConfigEntry<bool> DeathCameraEnabled;
        internal static ConfigEntry<float> DeathCameraHeight;
        internal static ConfigEntry<bool> DeathCameraLookAtKiller;

        /// <summary>True when manual screenshots are on for this player (admin and player switches both on).</summary>
        internal static bool ScreenshotsActive
        {
            get { return ModSettings.On && ScreenshotServerEnabled.Value && ScreenshotEnabled.Value; }
        }

        /// <summary>True when the in-game death shout and the Discord death message are on for this player.</summary>
        internal static bool DeathsActive
        {
            get { return ModSettings.On && DeathServerEnabled.Value && DeathEnabled.Value; }
        }

        /// <summary>
        /// True when this game records death videos, i.e. keeps the rolling frame buffer running.
        /// The boss death video reuses those frames while this is true.
        /// </summary>
        internal static bool DeathVideoActive
        {
            get { return DeathsActive && DeathVideoEnabled.Value; }
        }




        internal static bool TryGetDeathResolution(out int width, out int height)
        {
            width = 960;
            height = 540;

            string value = DeathVideoResolution.Value;
            if (value == "480x270") { width = 480; height = 270; return true; }
            if (value == "640x360") { width = 640; height = 360; return true; }
            if (value == "960x540") { width = 960; height = 540; return true; }

            RelayDiagnostics.Warning("Unknown [Player Deaths] Client - Video Resolution '" + value + "'; using 960x540.");
            return true;
        }
    }
}
