using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// All [Clips] settings, bound once into these fields by Init().
    ///
    /// A clip is a short WebP video the player saves on demand with a hotkey: the last few seconds and the next few
    /// seconds, cut from the same in-memory frame buffer the death video uses (see ClientCaptureService.Clip.cs).
    /// It is uploaded straight from the player's game, never through the server.
    ///
    /// Same naming rules as every other category: "Server - ..." settings are synced from the server (admin-controlled
    /// through ServerSync), "Client - ..." settings are each player's own. A clip is taken only when both
    /// Enabled switches are on. The frame rate and resolution are NOT set here: clips reuse
    /// [Player Deaths] Client - Video FPS and Client - Video Resolution, because they share the death video's frames.
    /// </summary>
    internal static class ClipConfig
    {
        internal const string Section = "Clips";

        /// <summary>The name Discord shows as the sender of every clip.</summary>
        internal const string SenderName = "ValheimDiscordRelay Clips";

        /// <summary>
        /// Used only if Discord refuses <see cref="SenderName"/> (HTTP 400). Same name without the word "Discord".
        /// Once a clip has been accepted under this name it is used from then on, so the upload is not repeated every time.
        /// </summary>
        internal const string FallbackSenderName = "ValheimRelay Clips";

        // Server - * (synced)
        internal static ConfigEntry<bool> ServerEnabled;
        internal static ConfigEntry<string> WebhookUrl;

        // Client - * (per player)
        internal static ConfigEntry<bool> ClientEnabled;
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> IncludePlayerName;
        internal static ConfigEntry<int> PreDuration;
        internal static ConfigEntry<int> PostDuration;

        /// <summary>True when clips are on for this player (admin and player switches both on) and a hotkey is set.</summary>
        internal static bool Active
        {
            get { return ModSettings.On && ServerEnabled.Value && ClientEnabled.Value && Key.Value.MainKey != KeyCode.None; }
        }

        /// <summary>
        /// True when a clip could actually be sent right now: <see cref="Active"/> and a webhook is set. This is what keeps
        /// the rolling frame buffer running when death videos are off, so an unconfigured install pays nothing.
        /// </summary>
        internal static bool Ready
        {
            get { return Active && !string.IsNullOrWhiteSpace(WebhookUrl.Value); }
        }

    }
}
