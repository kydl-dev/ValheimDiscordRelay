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
            get { return ServerEnabled.Value && ClientEnabled.Value && Key.Value.MainKey != KeyCode.None; }
        }

        /// <summary>
        /// True when a clip could actually be sent right now: <see cref="Active"/> and a webhook is set. This is what keeps
        /// the rolling frame buffer running when death videos are off, so an unconfigured install pays nothing.
        /// </summary>
        internal static bool Ready
        {
            get { return Active && !string.IsNullOrWhiteSpace(WebhookUrl.Value); }
        }

        /// <summary>
        /// Binds every [Clips] setting. Called once, from SharedConfig.InitAll().
        /// </summary>
        internal static void Init()
        {
            ServerEnabled = SyncedConfig.Bind(
                Section, "Server - Enabled", true,
                "Allow clips to be sent to Discord. Synced from the server: turn off to disable the feature for every player. " +
                "Only admins can change it while [Admin] Server - Lock Configuration is on.");

            WebhookUrl = SyncedConfig.Bind(
                Section, "Server - Webhook URL", "",
                "Discord webhook URL used by clients for clips. Set by the server admin: the server's value is sent to every player, " +
                "and only admins can change it while [Admin] Server - Lock Configuration is on. Players' games receive this URL, so anyone who joins can read it: use a dedicated webhook/channel and delete the webhook in Discord if it leaks.");

            ClientEnabled = SharedConfig.File.Bind(
                Section, "Client - Enabled", true,
                "Allow the clip hotkey in this game. Turn off if you never want to send clips. " +
                "(The server must also have [Clips] Server - Enabled on.)");

            Key = SharedConfig.File.Bind(
                Section, "Client - Key", new KeyboardShortcut(KeyCode.PageDown),
                "Key, or two keys pressed together, that save a clip and send it to Discord. " +
                "Examples: PageDown, or F9 + LeftControl (hold both; the order you press them in does not matter). " +
                "Other keys held at the same time are ignored. Set to None to disable the hotkey.");

            IncludePlayerName = SharedConfig.File.Bind(
                Section, "Client - Include Player Name", true,
                "Add a line with your character name to the message that carries the clip, so the others can see who saved it. " +
                "Discord always shows \"" + SenderName + "\" as the sender.");

            PreDuration = SharedConfig.File.Bind(
                Section, "Client - Pre Duration", 4,
                new ConfigDescription(
                    "Seconds of footage before the key press that go into the clip. Allowed range is 2 to 4 (the frame buffer keeps about 4 seconds).",
                    new AcceptableValueRange<int>(2, 4)));

            PostDuration = SharedConfig.File.Bind(
                Section, "Client - Post Duration", 4,
                new ConfigDescription(
                    "Seconds of footage after the key press that go into the clip. Allowed range is 2 to 4. The clip is sent once this time has passed.",
                    new AcceptableValueRange<int>(2, 4)));
        }
    }
}
