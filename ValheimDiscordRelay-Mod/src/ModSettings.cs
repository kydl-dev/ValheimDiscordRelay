using System;
using BepInEx.Configuration;
using Kydl.Common;
using UnityEngine;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// The ONE config of the mod (PROJECT_RULES 11), bound once by <see cref="Bind"/> into ONE file, com.kydldev.ValheimDiscordRelay.cfg.
    /// [General] EnableAll switches the whole mod, [DebugLog] Enable adds detail lines to the log, every feature section has its own Enable.
    ///
    /// Key naming: "Server - ..." entries belong to the server admin. Most are synced (bound through <see cref="SyncedConfig"/>): every player
    /// receives the server's value and only an admin can change it. The ones only the server uses (private webhook URLs, notification switches) are
    /// plain entries, never sent to ordinary players; AdminSettingsChannel hands them to admins only. "Client - ..." entries are each player's own.
    /// Every entry shows in the KY hub (F6) on this mod's Settings tab; the fields live in the per-feature config classes (ClientConfig, ServerConfig,
    /// ClipConfig, BossConfig, PlayerNotificationConfig, ReportConfig, AdminConfig) that the rest of the code reads.
    /// </summary>
    internal static class ModSettings
    {
        /// <summary>The config file of the plugin (every entry below is bound into it).</summary>
        internal static ConfigFile File;

        internal static ConfigEntry<bool> EnableAll;
        internal static ConfigEntry<bool> DebugLog;

        /// <summary>Is the mod switched on ([General] EnableAll)? Off = inert: nothing is relayed, recorded, uploaded or reported.</summary>
        internal static bool On { get { return EnableAll == null || EnableAll.Value; } }

        internal static bool Verbose { get { return DebugLog != null && DebugLog.Value; } }

        private static bool _bound;

        /// <summary>Binds every entry, once, in the order the sections appear in the file and on the Settings tab.</summary>
        internal static void Bind(ConfigFile c)
        {
            if (_bound)
                return;
            _bound = true;
            File = c;

            EnableAll = c.Bind("General", "EnableAll", true,
                "Master switch. Off = the mod does nothing in this game: no chat relay, screenshots, clips, death or boss videos, notifications or weekly report, and no version check.");
            AdminConfig.LockConfiguration = SyncedConfig.BindLock("General", "Server - Lock Configuration", true,
                "When true, only players listed in the server's adminlist.txt can change the synced settings from their game; everyone else's edits are ignored. Set to false to let ANY connected player change them (and have the server save it).");
            AdminConfig.RequireSameVersion = AdminOnly.Bind("General", "Server - Require Same Version", true, new ConfigDescription(
                "When true, the server refuses every player who does not run exactly the same version of this mod as the server: " +
                "players without the mod, with an older version and with a newer version. They see Valheim's \"Incompatible version\" message; " +
                "the server log says who was refused and why. Applies to a dedicated server and to a world hosted from the game's menu. " +
                "Players who have the mod can still join other servers that do not have it. Set to false to let everybody in " +
                "(only players with a matching version then relay chat, deaths, kills and so on)."));
            AdminConfig.LogSuccessfulSends = SyncedConfig.Bind("General", "Server - Log Successful Sends", false,
                "Log every successful Discord webhook delivery, from every category: chat, server notifications, player notifications, boss deaths and the weekly report (logged by the server), " +
                "and screenshot / death video / boss video uploads (logged in the log of the game that uploaded them).");

            DebugLog = c.Bind("DebugLog", "Enable", false, "Also write DEBUG lines (what was captured, queued, sent or skipped, and why) to the BepInEx log.");

            BindChat(c);
            BindScreenshots(c);
            BindClips(c);
            BindPlayerDeaths(c);
            BindBossDeath(c);
            BindServerNotifications(c);
            BindPlayerNotifications(c);
            BindWeeklyReport(c);

            EnableAll.SettingChanged += (s, e) => ModLog.Info("Config: EnableAll = " + EnableAll.Value + (EnableAll.Value ? ", the mod is on" : ", the mod is inert"));
        }

        // ---- [Chat] --------------------------------------------------------------------------------------------------------------
        private static void BindChat(ConfigFile c)
        {
            ServerConfig.Enabled = SyncedConfig.Bind("Chat", "Enable", true,
                "Enable the Valheim chat -> Discord relay. Synced from the server. When off, nothing is posted to the chat webhooks.");
            ServerConfig.WebhookUrl = AdminOnly.Bind("Chat", "Server - Webhook URL", "",
                "Fallback Discord webhook URL. Used only when the specific Normal/Shout webhook is empty. Server-side only: players never receive it.");
            ServerConfig.NormalWebhookUrl = AdminOnly.Bind("Chat", "Server - Normal Webhook URL", "",
                "Discord webhook URL for normal chat. Server-side only: players never receive it.");
            ServerConfig.ShoutWebhookUrl = AdminOnly.Bind("Chat", "Server - Shout Webhook URL", "",
                "Discord webhook URL for shouts. Server-side only: players never receive it.");
            ServerConfig.MaxMessageLength = SyncedConfig.Bind("Chat", "Server - Max Message Length", 1800, new ConfigDescription(
                "Maximum message text sent to Discord. Discord content is additionally wrapped in an ANSI code block.",
                new AcceptableValueRange<int>(100, 1900)));
            ServerConfig.NameDisplay = SyncedConfig.Bind("Chat", "Server - Name Display", NameDisplayMode.NameOnly,
                "Discord webhook username format. NameOnly = Bjorn. NameWithNumber = Bjorn [1], Bjorn [2] for duplicate character names. NameWithIdSuffix = Bjorn [5678], using only the last 4 digits of the platform ID.");
            ServerConfig.ShoutAnsi = SyncedConfig.Bind("Chat", "Server - Shout Prefix", "\u001b[2;31m",
                "ANSI escape sequence placed before a shout.");
            ServerConfig.NormalAnsi = SyncedConfig.Bind("Chat", "Server - Normal Prefix", "\u001b[2;36m",
                "ANSI escape sequence placed before normal chat.");
            ServerConfig.QueueLimit = SyncedConfig.Bind("Chat", "Server - Queue Limit", 100, new ConfigDescription(
                "Maximum queued Discord messages. Oldest messages are discarded if the queue is full. The weekly report's messages are not counted and never discarded.",
                new AcceptableValueRange<int>(10, 1000)));
            ServerConfig.SendIntervalMs = SyncedConfig.Bind("Chat", "Server - Minimum Send Interval Ms", 250, new ConfigDescription(
                "Minimum delay between webhook requests. This is intentionally paced to avoid webhook rate-limit bursts.",
                new AcceptableValueRange<int>(50, 5000)));
            Client.ClientConfig.ChatEnabled = c.Bind("Chat", "Client - Enabled", true,
                "Send the chat you type in this game to the server so it can be relayed to Discord. Turn off to keep your own chat out of Discord. " +
                "(The server must also have [Chat] Enable on.)");
            Client.ClientConfig.ChatMaxMessageLength = c.Bind("Chat", "Client - Max Message Length", 1000, new ConfigDescription(
                "Maximum characters sent by the client. The server applies its own limit too.",
                new AcceptableValueRange<int>(1, 1000)));
        }

        // ---- [Screenshots] -------------------------------------------------------------------------------------------------------
        private static void BindScreenshots(ConfigFile c)
        {
            Client.ClientConfig.ScreenshotServerEnabled = SyncedConfig.Bind("Screenshots", "Enable", true,
                "Allow manual screenshots to be sent to Discord. Synced from the server: turn off to disable the feature for every player. " +
                "Only admins can change it while [General] Server - Lock Configuration is on.");
            Client.ClientConfig.ScreenshotWebhookUrl = SyncedConfig.Bind("Screenshots", "Server - Webhook URL", "",
                "GLOBAL screenshot webhook: the normal screenshots go here, and so do the screenshots without HUD (and without HUD and player) whose own webhook below is empty. Discord webhook URL used by clients for manual screenshots. Set by the server admin: the server's value is sent to every " +
                "player, and only admins can change it while [General] Server - Lock Configuration is on. Players' games receive this URL, so anyone who joins can read it: use a dedicated webhook/channel and delete the webhook in Discord if it leaks.");
            Client.ClientConfig.ScreenshotHudHiddenWebhookUrl = SyncedConfig.Bind("Screenshots", "Server - HUD Hidden Webhook URL", "",
                "Discord webhook URL for the screenshots WITHOUT HUD. Empty = they go to [Screenshots] Server - Webhook URL. Synced like the global one: players' games receive it, so anyone who joins can read it. " +
                "Only admins can change it while [General] Server - Lock Configuration is on.");
            Client.ClientConfig.ScreenshotHudPlayerHiddenWebhookUrl = SyncedConfig.Bind("Screenshots", "Server - HUD And Player Hidden Webhook URL", "",
                "Discord webhook URL for the screenshots WITHOUT HUD and WITHOUT the player's character. Empty = they go to [Screenshots] Server - Webhook URL. Synced like the global one: players' games receive it, so anyone who joins can read it. " +
                "Only admins can change it while [General] Server - Lock Configuration is on.");
            Client.ClientConfig.ScreenshotNormalEnabled = c.Bind("Screenshots", "Client - Normal Enabled", true,
                "Allow the normal screenshot (HUD on) in this game. Off = its key does nothing and its button in the F6 hub is hidden. [Screenshots] Client - Enabled switches all three kinds.");
            Client.ClientConfig.ScreenshotHudHiddenEnabled = c.Bind("Screenshots", "Client - HUD Hidden Enabled", true,
                "Allow the screenshot without HUD in this game. Off = its key does nothing and its button in the F6 hub is hidden.");
            Client.ClientConfig.ScreenshotHudPlayerHiddenEnabled = c.Bind("Screenshots", "Client - HUD And Player Hidden Enabled", true,
                "Allow the screenshot without HUD and without your character in this game. Off = its key does nothing and its button in the F6 hub is hidden.");
            Client.ClientConfig.ScreenshotEnabled = c.Bind("Screenshots", "Client - Enabled", true,
                "Allow screenshots in this game: off = none of the three kinds works. Turn off if you never want to send screenshots. " +
                "(The server must also have [Screenshots] Enable on.)");
            Client.ClientConfig.ScreenshotKey = c.Bind("Screenshots", "Client - Key", new KeyboardShortcut(KeyCode.PageUp),
                "Key that captures the current rendered Valheim screen and sends it to Discord. Set to None to disable the hotkey.");
            Client.ClientConfig.ScreenshotHudHiddenKey = c.Bind("Screenshots", "Client - HUD Hidden Key", new KeyboardShortcut(KeyCode.P),
                "Key that hides the whole HUD (like Ctrl+F3), takes a screenshot and shows the HUD again. Does nothing while you type in the chat, the console or a text box. Set to None to disable.");
            Client.ClientConfig.ScreenshotHudPlayerHiddenKey = c.Bind("Screenshots", "Client - HUD And Player Hidden Key", new KeyboardShortcut(KeyCode.P, KeyCode.LeftAlt),
                "Key that hides the HUD and your own character, takes a screenshot and brings both back. Does nothing while you type in the chat, the console or a text box. Set to None to disable.");
            Client.ClientConfig.IncludeScreenshotPlayerName = c.Bind("Screenshots", "Client - Include Player Name", true,
                "Use the local Valheim character name as the Discord webhook username for manual screenshots.");
        }

        // ---- [Clips] -------------------------------------------------------------------------------------------------------------
        private static void BindClips(ConfigFile c)
        {
            Client.ClipConfig.ServerEnabled = SyncedConfig.Bind("Clips", "Enable", true,
                "Allow clips to be sent to Discord. Synced from the server: turn off to disable the feature for every player. " +
                "Only admins can change it while [General] Server - Lock Configuration is on.");
            Client.ClipConfig.WebhookUrl = SyncedConfig.Bind("Clips", "Server - Webhook URL", "",
                "Discord webhook URL used by clients for clips. Set by the server admin: the server's value is sent to every player, " +
                "and only admins can change it while [General] Server - Lock Configuration is on. Players' games receive this URL, so anyone who joins can read it: use a dedicated webhook/channel and delete the webhook in Discord if it leaks.");
            Client.ClipConfig.ClientEnabled = c.Bind("Clips", "Client - Enabled", true,
                "Allow the clip hotkey in this game. Turn off if you never want to send clips. " +
                "(The server must also have [Clips] Enable on.)");
            Client.ClipConfig.Key = c.Bind("Clips", "Client - Key", new KeyboardShortcut(KeyCode.PageDown),
                "Key, or two keys pressed together, that save a clip and send it to Discord. " +
                "Examples: PageDown, or F9 + LeftControl (hold both; the order you press them in does not matter). " +
                "Other keys held at the same time are ignored. Set to None to disable the hotkey.");
            Client.ClipConfig.IncludePlayerName = c.Bind("Clips", "Client - Include Player Name", true,
                "Add a line with your character name to the message that carries the clip, so the others can see who saved it. " +
                "Discord always shows \"" + Client.ClipConfig.SenderName + "\" as the sender.");
            Client.ClipConfig.PreDuration = c.Bind("Clips", "Client - Pre Duration", 4, new ConfigDescription(
                "Seconds of footage before the key press that go into the clip. Allowed range is 2 to 4 (the frame buffer keeps about 4 seconds).",
                new AcceptableValueRange<int>(2, 4)));
            Client.ClipConfig.PostDuration = c.Bind("Clips", "Client - Post Duration", 4, new ConfigDescription(
                "Seconds of footage after the key press that go into the clip. Allowed range is 2 to 4. The clip is sent once this time has passed.",
                new AcceptableValueRange<int>(2, 4)));
        }

        // ---- [Player Deaths] -----------------------------------------------------------------------------------------------------
        private static void BindPlayerDeaths(ConfigFile c)
        {
            Client.ClientConfig.DeathServerEnabled = SyncedConfig.Bind("Player Deaths", "Enable", true,
                "Enable player death messages (in-game shout and Discord message, with a video when videos are on). Synced from the server: " +
                "turn off to disable the feature for every player. Only admins can change it while [General] Server - Lock Configuration is on.");
            Client.ClientConfig.DeathVideoWebhookUrl = SyncedConfig.Bind("Player Deaths", "Server - Webhook URL", "",
                "Discord webhook URL used by clients for death videos and death messages. Set by the server admin: the server's value is sent " +
                "to every player, and only admins can change it while [General] Server - Lock Configuration is on. Players' games receive this URL, so anyone who joins can read it: use a dedicated webhook/channel and delete the webhook in Discord if it leaks.");
            Client.ClientConfig.DeathEnabled = c.Bind("Player Deaths", "Client - Enabled", true,
                "Announce this player's deaths: the in-game death shout and the Discord death message. Turn off to keep your deaths out of chat and Discord. " +
                "(The server must also have [Player Deaths] Enable on.)");
            Client.ClientConfig.DeathVideoEnabled = c.Bind("Player Deaths", "Client - Video Enabled", true,
                "Create a short WebP video around the local player's death. When off, the death message is still sent, without a video. " +
                "This also keeps the rolling frame buffer that boss death videos can reuse (see [Boss Death]).");
            Client.ClientConfig.DeathVideoFps = c.Bind("Player Deaths", "Client - Video FPS", 30, new ConfigDescription(
                "Frames per second for death videos.",
                new AcceptableValueList<int>(20, 30)));
            Client.ClientConfig.DeathVideoResolution = c.Bind("Player Deaths", "Client - Video Resolution", "960x540", new ConfigDescription(
                "Death video resolution.",
                new AcceptableValueList<string>("480x270", "640x360", "960x540")));
            Client.ClientConfig.DeathVideoPreDuration = c.Bind("Player Deaths", "Client - Video Pre Duration", 4, new ConfigDescription(
                "Seconds captured before death. Allowed range is 2 to 4.",
                new AcceptableValueRange<int>(2, 4)));
            Client.ClientConfig.DeathVideoPostDuration = c.Bind("Player Deaths", "Client - Video Post Duration", 4, new ConfigDescription(
                "Seconds captured after death. Allowed range is 2 to 4.",
                new AcceptableValueRange<int>(2, 4)));
            Client.ClientConfig.DeathCameraEnabled = c.Bind("Player Deaths", "Client - Death Camera", true,
                "After you die, smoothly lift the camera up and look down at your body, so the death video ends on a cinematic shot. " +
                "It also moves your own view for those few seconds. Only used while a death video is being recorded.");
            Client.ClientConfig.DeathCameraHeight = c.Bind("Player Deaths", "Client - Death Camera Height", 6f, new ConfigDescription(
                "How many metres the death camera rises above where it was when you died. It stops short of ceilings and other obstacles.",
                new AcceptableValueRange<float>(1f, 15f)));
            Client.ClientConfig.DeathCameraLookAtKiller = c.Bind("Player Deaths", "Client - Death Camera Look At Killer", true,
                "While rising, turn the camera from your body towards whatever killed you, as far as it can without losing your body from view. " +
                "Has no effect for deaths without a killer (falling, drowning, ...).");
        }

        // ---- [Boss Death] --------------------------------------------------------------------------------------------------------
        private static void BindBossDeath(ConfigFile c)
        {
            BossConfig.Enabled = SyncedConfig.Bind("Boss Death", "Enable", true,
                "Announce boss deaths in Discord (embed) and, optionally, with a short video. Synced from the server: turn off to disable the feature for every player.");
            BossConfig.WebhookUrl = SyncedConfig.Bind("Boss Death", "Server - Webhook URL", "",
                "Discord webhook URL for boss death announcements. The server posts the embed to it; the player whose game " +
                "owns the boss when it dies uploads the video to it. Set by the server admin: the server's value is sent to every player, " +
                "and only admins can change it while [General] Server - Lock Configuration is on. Players' games receive this URL, so anyone who joins can read it: use a dedicated webhook/channel and delete the webhook in Discord if it leaks.");
            BossConfig.WebhookUsername = SyncedConfig.Bind("Boss Death", "Server - Webhook Username", "Boss Death Announcement!",
                "Name shown as the sender of the embed and of the video. Synced from the server; only admins can change it while [General] Server - Lock Configuration is on.");
            BossConfig.VideoSource = SyncedConfig.Bind("Boss Death", "Server - Video Source", BossConfig.VideoSourceOwner, new ConfigDescription(
                "Whose game records the boss death video. \"Boss Owner\" (default): the player whose game owns the boss when it dies. " +
                "\"Last Attacker\": the player who landed the last hit, who is almost certainly facing the boss. " +
                "NOTE for admins: with \"Last Attacker\" the video is recorded by that player's own game, so THEIR client settings apply: " +
                "[Player Deaths] Client - Video FPS and Client - Video Resolution (and [Player Deaths] Client - Video Enabled must be on), plus their own " +
                "[Boss Death] Client - Enabled, Client - Video Enabled and Client - Video Pre/Post Duration. Your settings and the boss owner's settings are not used. " +
                "If the last attacker cannot record (mod missing, video off, busy, no reply), the boss owner's video is used instead. " +
                "Synced from the server; only admins can change it while [General] Server - Lock Configuration is on.",
                new AcceptableValueList<string>(BossConfig.VideoSourceOwner, BossConfig.VideoSourceLastAttacker)));
            BossConfig.ClientEnabled = c.Bind("Boss Death", "Client - Enabled", true,
                "Take part in boss death announcements from this game: measure the damage you deal to bosses and record the boss death video when asked. " +
                "Turn off to opt out completely. (The server must also have [Boss Death] Enable on.)");
            BossConfig.VideoEnabled = c.Bind("Boss Death", "Client - Video Enabled", true,
                "Upload a short video of the moment the boss died. Only the client that records it (see Server - Video Source) needs this on.");
            BossConfig.VideoFps = c.Bind("Boss Death", "Client - Video FPS", 30, new ConfigDescription(
                "Frames per second for the boss death video. Only used when [Player Deaths] Client - Video Enabled is off; otherwise the boss video reuses the death video frames and its FPS.",
                new AcceptableValueList<int>(20, 30)));
            BossConfig.VideoResolution = c.Bind("Boss Death", "Client - Video Resolution", "960x540", new ConfigDescription(
                "Boss death video resolution. Only used when [Player Deaths] Client - Video Enabled is off; otherwise the boss video reuses the death video frames and its resolution.",
                new AcceptableValueList<string>("480x270", "640x360", "960x540")));
            BossConfig.VideoPreDuration = c.Bind("Boss Death", "Client - Video Pre Duration", 4, new ConfigDescription(
                "Seconds captured before the boss dies. Allowed range is 2 to 4.",
                new AcceptableValueRange<int>(2, 4)));
            BossConfig.VideoPostDuration = c.Bind("Boss Death", "Client - Video Post Duration", 4, new ConfigDescription(
                "Seconds captured after the boss dies. Allowed range is 2 to 4.",
                new AcceptableValueRange<int>(2, 4)));
        }

        // ---- [Server Notifications] ----------------------------------------------------------------------------------------------
        private static void BindServerNotifications(ConfigFile c)
        {
            ServerConfig.ServerNotificationsEnabled = AdminOnly.Bind("Server Notifications", "Enable", true,
                "Enable server up/down/restart notifications in a dedicated Discord channel/webhook. (Player join/leave messages have their own [Player Notifications] section.)");
            ServerConfig.ServerNotificationsWebhookUrl = AdminOnly.Bind("Server Notifications", "Server - Webhook URL", "",
                "Discord webhook URL for server up/down/restart notifications. Server-side only: players never receive it.");
            ServerConfig.UseDiscordWebhookName = SyncedConfig.Bind("Server Notifications", "Server - Use Discord Webhook Name", false,
                "Unchecked: messages are sent with the username \"" + ServerConfig.ServerNotificationsWebhookName + "\". " +
                "Checked: no username is sent, so Discord shows the name that is set on the webhook itself (Channel settings > Integrations > Webhooks). " +
                "Synced from the server; only admins can change it while [General] Server - Lock Configuration is on.");
            ServerConfig.ServerUpNotification = AdminOnly.Bind("Server Notifications", "Server - Up Notification", true,
                "Send a notification when the Valheim server starts. A start within Server - Restart Window Seconds of a clean shutdown is reported as a restart instead.");
            ServerConfig.ServerDownNotification = AdminOnly.Bind("Server Notifications", "Server - Down Notification", true,
                "Send a notification when the Valheim server shuts down cleanly.");
            ServerConfig.ServerRestartNotification = AdminOnly.Bind("Server Notifications", "Server - Restart Notification", true,
                "Send a notification when the server starts shortly after a clean shutdown (see Server - Restart Window Seconds).");
            ServerConfig.ServerRestartWindowSeconds = AdminOnly.Bind("Server Notifications", "Server - Restart Window Seconds", 120, new ConfigDescription(
                "A startup counts as a restart only if it happens within this many seconds of the last clean shutdown. " +
                "The time includes the server booting and loading the world. Later startups are reported as Server Up. " +
                "Raise it if your server takes longer than this to come back during a real restart.",
                new AcceptableValueRange<int>(10, 3600)));
        }

        // ---- [Player Notifications] ----------------------------------------------------------------------------------------------
        private static void BindPlayerNotifications(ConfigFile c)
        {
            PlayerNotificationConfig.Enabled = SyncedConfig.Bind("Player Notifications", "Enable", true,
                "ON: Valheim's own arrival message (\"I have arrived!\") is intercepted and NOT sent to Discord. Instead the mod posts " +
                "\"<name> has joined the world. Population: <n>. Good luck!\" and \"<name> had to leave us. Hope they had fun. Population: <n>.\" " +
                "to this section's webhook. OFF: the game's arrival message is relayed like any other shout through the normal chat webhook, " +
                "and no join/leave messages are posted. Synced from the server.");
            PlayerNotificationConfig.WebhookUrl = AdminOnly.Bind("Player Notifications", "Server - Webhook URL", "",
                "Discord webhook URL for the join/leave messages. Messages are sent as \"" + PlayerNotificationConfig.WebhookName + "\". Server-side only: players never receive it.");
            PlayerNotificationConfig.ArrivalAvatarUrl = SyncedConfig.Bind("Player Notifications", "Server - Arrival Avatar URL", "",
                "Image URL (http/https) used as the webhook icon for join messages. Leave empty to use the webhook's default Discord icon. " +
                "Synced from the server; only admins can change it while Lock Configuration is on.");
            PlayerNotificationConfig.LeaveAvatarUrl = SyncedConfig.Bind("Player Notifications", "Server - Leave Avatar URL", "",
                "Image URL (http/https) used as the webhook icon for leave messages. Leave empty to use the webhook's default Discord icon. " +
                "Synced from the server; only admins can change it while Lock Configuration is on.");
        }

        // ---- [Weekly Report] -----------------------------------------------------------------------------------------------------
        private static void BindWeeklyReport(ConfigFile c)
        {
            ReportConfig.Enabled = SyncedConfig.Bind("Weekly Report", "Enable", true,
                "Collect weekly statistics (players joined, mob kills, deaths, boss kills, damage) and post them as one embed " +
                "once a week. Synced from the server: turn off to disable the feature for every player.");
            ReportConfig.WebhookUrl = AdminOnly.Bind("Weekly Report", "Server - Webhook URL", "",
                "Discord webhook URL for the weekly report. Messages are sent as \"" + ReportConfig.WebhookName + "\". Server-side only: players never receive it.");
            ReportConfig.ReportDay = SyncedConfig.Bind("Weekly Report", "Server - Report Day", DayOfWeek.Sunday,
                "Day of the week the report is posted. Synced from the server; only admins can change it while [General] Server - Lock Configuration is on.");
            ReportConfig.ReportTime = SyncedConfig.Bind("Weekly Report", "Server - Report Time", "20:00",
                "Time of day the report is posted, 24-hour HH:mm, in the SERVER's local time zone. Synced from the server; only admins can change it while [General] Server - Lock Configuration is on.");
            ReportConfig.SendTestReport = SyncedConfig.Bind("Weekly Report", "Server - Send Test Report", false,
                "Debug helper. Set to true to post the report as it stands right now to the webhook, marked as a test. " +
                "Once set to true, the report is being sent and this option gets set to false instantly." +
                "It does not reset the statistics, and the setting turns itself back to false. " +
                "It fires when the setting changes (an admin in the KY hub, or a live config reload on the server) or at the next server start. " +
                "Synced from the server; only admins can trigger it while [General] Server - Lock Configuration is on.");
            ReportConfig.WindowEnabled = SyncedConfig.Bind("Weekly Report", "Server - Window Enabled", true,
                "Let players open the in-game weekly report window, which shows the same numbers as the Discord report as they stand " +
                "right now. Synced from the server: turn off to disable the window for every player. It has no effect on the Discord report " +
                "or on what is collected. Only admins can change it while [General] Server - Lock Configuration is on.");
            ReportConfig.ClientEnabled = c.Bind("Weekly Report", "Client - Enabled", true,
                "Include this player in the weekly report: this game measures the kills and damage you deal and reports your deaths to the server. " +
                "Turn off to opt out. (The server must also have [Weekly Report] Enable on.)");
            ReportConfig.WindowKey = c.Bind("Weekly Report", "Client - Window Key", new KeyboardShortcut(KeyCode.Home),
                "Key, or two keys pressed together, that open and close the in-game weekly report window. " +
                "Examples: Home, or F8 + LeftControl (hold both; the order you press them in does not matter). " +
                "Set to None to disable the hotkey. Escape also closes the window.");
            ReportConfig.WindowScale = c.Bind("Weekly Report", "Client - Window Scale", 0f, new ConfigDescription(
                "Size of the in-game weekly report window. 0 = automatic (follows your screen height: normal size at 1080p, " +
                "larger on taller screens). Otherwise 0.7 to 2.0, where 1.0 is the normal size.",
                new AcceptableValueRange<float>(0f, 2f)));
            ReportConfig.WindowStyle = c.Bind("Weekly Report", "Client - Window Style", ReportWindowStyle.Classic,
                "Look of the in-game weekly report window. Classic = the original flat look. Modern = rounded cards, pill-shaped tabs, " +
                "a gold primary button, key hints in the footer and Valheim's own fonts. Only the look changes: the same numbers, tabs, " +
                "keys and window size. It applies the next time the window opens. Each style has its own style sheet in the config folder " +
                "(ValheimDiscordRelay.report.uss for Classic, ValheimDiscordRelay.report.modern.uss for Modern) that you can edit.");
            ReportConfig.WindowWidth = c.Bind("Weekly Report", "Client - Window Width", 0f, new ConfigDescription(
                "Width of the in-game weekly report window, remembered between sessions. It is saved automatically when you drag the " +
                "window's bottom-right corner, so there is normally no need to edit it. 0 = the default size. The window is never " +
                "made narrower than its content needs. The unit is the window's own size before Client - Window Scale is applied.",
                new AcceptableValueRange<float>(0f, 4000f)));
            ReportConfig.WindowHeight = c.Bind("Weekly Report", "Client - Window Height", 0f, new ConfigDescription(
                "Height of the in-game weekly report window, remembered between sessions. It is saved automatically when you drag the " +
                "window's bottom-right corner, so there is normally no need to edit it. 0 = the default size. The window is never " +
                "made lower than its content needs. The unit is the window's own size before Client - Window Scale is applied.",
                new AcceptableValueRange<float>(0f, 4000f)));
        }
    }
}
