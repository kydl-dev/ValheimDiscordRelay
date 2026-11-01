# Changelog

## 2.0.0

**Breaking: client AND server must update together, and the config file is new.**

### New
- **Two more kinds of screenshot.** Besides the normal screenshot (`PageUp`, HUD on, unchanged):
  - **`P`: screenshot without HUD.** Every HUD element is hidden the way Ctrl+F3 hides it, the screenshot is taken, then the HUD comes back (it stays hidden if you had hidden it yourself with Ctrl+F3).
  - **`Alt` + `P`: screenshot without HUD and without your own character.** The same, and your character (with armour, weapons and shadow) is hidden for the moment of the capture.
  - The uploaded file names end in `-nohud` and `-nohud-noplayer`. `P` and `Alt` + `P` are not bound by Valheim itself. Both can be changed or switched off in `[Screenshots]` (`Client - HUD Hidden Key`, `Client - HUD And Player Hidden Key`).
  - The two new keys do nothing while you type in the chat, the console or a text box. The normal screenshot key works at any time, also while the chat is open.
  - The death-video frame fallback is never used for the two new kinds, because those frames still show the HUD.
  - **Three webhooks.** `[Screenshots] Server - Webhook URL` is the global one (normal screenshots). `Server - HUD Hidden Webhook URL` and `Server - HUD And Player Hidden Webhook URL` are optional: when one is empty, that kind goes to the global webhook. All three are synced like the global one (players' games upload to them, so they are not secret).
  - **The player can switch each kind off.** `[Screenshots] Client - Enabled` still switches all screenshots; `Client - Normal Enabled`, `Client - HUD Hidden Enabled` and `Client - HUD And Player Hidden Enabled` switch one kind each. A kind that is off (or has no webhook) does nothing when its key is pressed and has no button in the F6 hub.
- **A "Screenshots" tab in the KY hub (F6)** with one button per kind. A button closes the window and takes that screenshot, the same as pressing its key. A kind that is switched off, or has no webhook, has no button. The Settings tab stays as it was.

### Changed
- **One plugin, one new GUID.** The two plugins (`com.valheimdiscordrelay.client` and `.server`) are now ONE plugin, `com.kydldev.ValheimDiscordRelay`. What runs is decided at run time: the server parts act only when the game is a server (dedicated or hosted), the capture parts only in a game with a screen. A hosted world runs both, as before.
- **One config file: `BepInEx/config/com.kydldev.ValheimDiscordRelay.cfg`.** Copy your values from the old `ValheimDiscordRelay.cfg` into the new file (sections and key names are the same, except the ones listed below), then delete the old file. The report window's style sheets (`ValheimDiscordRelay.report.uss`, `...report.modern.uss`) and the saved statistics keep their names and keep working.
- **Needs KydlCore and Jotunn** (also on a dedicated server): the KY hub (F6) and the shared log come from KydlCore.
- **Every setting is in the KY hub (F6), Settings tab.** Synced `Server - ...` settings can be changed there by an admin and show as locked ("Set by the server") for everybody else; `Client - ...` settings are each player's own. Settings only the server uses (the private webhook URLs, the server notification switches) are handed to admins only, never to ordinary players, so an admin can edit them from the game too.
- **Config layout follows the KydlDev rule:** `[General] EnableAll` (master switch, on by default; off = the mod does nothing), `[DebugLog] Enable` (off by default) and an `Enable` entry in every feature section.
  - Renamed: `Server - Enabled` of `[Chat]`, `[Screenshots]`, `[Player Deaths]`, `[Clips]`, `[Boss Death]`, `[Server Notifications]`, `[Player Notifications]` and `[Weekly Report]` is now `Enable`.
  - Moved: the `[Admin]` settings (`Server - Lock Configuration`, `Server - Require Same Version`, `Server - Log Successful Sends`) are now in `[General]`.
- **Logging:** the mod no longer keeps its own `ValheimDiscordRelay.log`; everything goes to the BepInEx log through the shared logger. DEBUG lines are written only while `[DebugLog] Enable` is on, and a line repeated within 2.5 seconds is written once.

### Fixed
- **A chat line is no longer lost when Discord refuses the player's name.** Discord answers HTTP 400 for a sender name that contains "discord" or "clyde". The line is now sent once more under the name "Valheim Player", with the real name in bold at the start of the text.
- **A very long player name that ends in an emoji** is cut without breaking the emoji in half (a broken emoji made Discord refuse the message).
- **A restart of the webhook sender in the same game process** (a new server session) sends messages again; before, it could stay in "shutting down" mode.
- **Clips are posted as "ValheimRelay Clips"** (was "ValheimDiscordRelay Clips"): Discord refuses sender names that contain "discord", so every game start first wasted an upload on the refused name. If Discord should refuse the new name too, the second try uses "Valheim Clips".
- **A failure while the boss death video frames are prepared** is logged and no longer ends the game process.

## 1.3.5

### New
- **Modern look for the in-game report window.** New setting `[Weekly Report] Client - Window Style` (`Classic` = the existing look, the default; `Modern`). Modern has rounded corners, rounded summary cards, pill-shaped tabs with a gold selected tab, a gold **Refresh** button, a round chart icon and a diamond on the line under the title, key hints in the footer (`Drag`, `Esc`, and your window key) and Valheim's Norse font for the title and the footer buttons. Same numbers, tabs, keys, layout and window size as Classic; the choice applies the next time the window opens (the window is built again when the style changed). Each style has its own style sheet file in the config folder: `ValheimDiscordRelay.report.uss` (Classic, as before) and `ValheimDiscordRelay.report.modern.uss` (Modern). In the Modern footer the standing note "Mob kills and damage are counted ..." is replaced by the key hints; the "You opted out" note still shows when it applies.
  - **Valheim's fonts in style sheets:** `-unity-font-definition: heading | heading-bold | body | body-bold` (Norse, Norsebold, Averia Serif Regular / Bold; names as in Jotunn). Only dynamic fonts are used; a font the game does not have is skipped with one log line. New `Report/ReportFonts.cs`, `Report/ReportModernStyle.cs`.
- **Version check on connect.** The server now refuses every player who does not run exactly the server's version of this mod: players without the mod, with an older version and with a newer version (they see Valheim's "Incompatible version"; the server log names the player and the version it found). New setting `[Admin] Server - Require Same Version` (default `true`, server only, not synced) turns it off. Applies to dedicated servers and worlds hosted from the menu. It only restricts who joins your server: a player who has the mod can still join servers without it. Builds from before this check are refused too, because they do not announce their version.
  - Done in the bundled ServerSync copy (`Common/ServerSync/ConfigSync.cs`, change 4 in its header): clients now always announce the mod's version, and a server with `RequireExactVersion` on refuses a client that announced nothing or another version. `Server/VersionEnforcement.cs` switches it on when the game is a server and the setting is on.
- **Redesigned Discord weekly report: one message per tab.** The report now has the same six tabs as the in-game window, each as its own Discord message with its own colour: Players, Bosses, Mobs killed, Killer mobs, Death causes and Highest hits (columns as in the window; every ranking shows a top 5 for the server and for each player, everything is still tracked in full).
  - Tables wrap long names onto a second line instead of cutting them with "...", and a column is as wide as its longest text needs.
  - The Bosses tab has one block per boss kill (Killing blow, Highest hit received, Kills, When; the time is shown in each reader's own time zone).
  - A tab that does not fit in one message continues in the next ("Page 2/3"); nothing is dropped except past 24 messages, 40 per-player tables per tab and 150 players in the Players table.
  - The report counts as delivered when Discord accepted every message; a retry after a partial failure sends only the missing messages. The test report (`Send Test Report`) sends all messages too.
- **Clips.** New `[Clips]` section: press a hotkey (default `PageDown`, or two keys together such as `F9 + LeftControl`) and the 4 seconds before and the 4 seconds after the press are sent to Discord as an animated WebP, as `ValheimDiscordRelay Clips`, uploaded straight from the client like the death video. Settings: `Server - Enabled`, `Server - Webhook URL` (synced), `Client - Enabled`, `Client - Key`, `Client - Include Player Name`, `Client - Pre Duration`, `Client - Post Duration`. Frame rate and resolution come from `[Player Deaths]`.
  - Clips are cut from the death video's in-memory frame buffer, so nothing extra is captured; the buffer also runs when only clips are ready, so clips work with death videos switched off.
  - A notice at the top left says when a clip was sent or failed (shown after the clip is finished, so it is not in the video). Quitting the game cancels a clip in progress. If Discord refuses the sender name (HTTP 400) the clip is retried once as `ValheimRelay Clips`.
  - The clip key ignores keys that are not part of the shortcut and does nothing while you are typing in the chat, the console or a sign / map-pin text box.
- **Highest single hit in the weekly report.** Each player's highest single hit (damage, weapon, enemy) is tracked, saved across restarts and shown in the Discord report, the report window and the report snapshots.
  - The weapon is always an English name, whatever language the game uses, and is read from what the attacking player holds, also when another player's game owns the creature. The hit is credited to the player who landed it.
  - Games send a new best hit only when it beats the one they sent before, at most every five seconds, and forget it when the server starts a new period (new routed RPC `ValheimDiscordRelay.ReportPeriodReset.v1`). A game that has no connection to the server keeps its damage / kills batch and sends it at the next flush.
- **Per-mob rankings in the weekly report.** Most killed mobs, mobs and causes that killed players, and the enemy of a highest hit use English names: the game's own English text when available, otherwise a built-in table of the vanilla creatures, otherwise words built from the key or prefab (`Greydwarf_Elite` shows as "Greydwarf Brute"). The per-mob kill counts are saved across restarts. A death caused by another player is recorded as the cause "Killed by a player", not as a mob.
- **In-game weekly report window.** Press `Home` (default) to see this week's report so far in a window in the game: summary tiles, a sortable **Players** tab (online players green, you gold), **Bosses**, **Mob rankings**, **Death causes** and **Highest hits** tabs, and the time of the next report in your own time zone. Refreshes every 30 seconds and with **Refresh**. Read-only.
  - The window opens compact (640 x 536), is never smaller than its content, and the size you drag it to is remembered (`Client - Window Width` / `Height`). Long lists scroll.
  - While it is open the character does not move or attack, the mouse cursor is free and `Escape` closes it. These patches are applied one by one, so one failing after a game update cannot stop the rest of the mod from loading.
  - New `[Weekly Report]` settings: `Server - Window Enabled` (synced, default on), `Client - Window Key` (`None` disables it), `Client - Window Scale` (`0` = automatic).
  - The server answers with routed RPCs `ValheimDiscordRelay.ReportSnapshotRequest.v1` / `...ReportSnapshotReply.v1` (one request per player per second at most). The reply is capped to the top 100 players, the last 50 boss kills and the top 5 of each ranking per player; the totals are always complete. Players' games accept replies and period resets only from the server.
- **Safer weekly report delivery.** The Discord embed is trimmed to Discord's limits (1,024 characters per field, 25 fields, 6,000 in all): the highest-hits list is capped with "...and N more" and the least important sections go first when the embed is too big.
- **Restart-safe counting.** The counted boss kills are saved with the statistics (new `bosskey` lines in `ValheimDiscordRelay.WeeklyReport.txt`, newest 128, entries older than 24 hours are not loaded) and announced bosses are saved in `BepInEx/cache/ValheimDiscordRelay.BossAnnounced.txt` (last 64). New `Common/RememberedKeys.cs`.
- Build tag in the log is now `mobs-killed` (later builds of 1.3.5 change it).

### Changed
- **Modern report window, footer buttons:** the **Refresh** and **Close** labels are larger (font size 15 -> 19) so the thin Norse font is easier to read; the buttons are 16 px wider (96 -> 112) to fit. Players with an existing `ValheimDiscordRelay.report.modern.uss` keep their old file: change `.vdr-btn-footer { font-size }` there or delete the file to get the new default.
- **In-game report, Players tab:** the columns are now Player, **Mobs Killed**, **Bosses killed** (new: boss kills where the player landed the killing blow, counted from the snapshot's boss kill list, so no server change), **Deaths** and **Total Damage**, all sortable. A player's name wraps onto more lines instead of being cut with "...", the row grows with it. The default window is 80 px wider so the name column has room (a size you saved yourself is kept).
- **The in-game weekly report window now uses Unity's UI Toolkit instead of IMGUI.** Same window, tabs, numbers and settings. It is built from code when the window first opens (no asset bundle, no UXML), is only updated when something changes instead of being redrawn every frame, and uses UI Toolkit pointer events for dragging, resizing, clicking and scrolling (own scrollbar, mouse wheel moves three rows). The per-player blocks on the Mob rankings tab are now tall enough for five rows (the fifth row used to run into the next heading). **The window can be styled with CSS:** its look comes from a USS-style sheet (a subset of Unity's USS: class, name and type selectors, `:hover`/`:active`/`:disabled`, `:root` variables with `var()`, colours, lengths and most style properties). Unity cannot read USS text at runtime without an asset bundle, so the mod reads it itself (`ReportStyleSheet`). The default rules are written to `BepInEx/config/ValheimDiscordRelay.report.uss` on first use and are read again each time the window opens. Build references: `UnityEngine.UIElementsModule` (and the two TextCore modules) added, `UnityEngine.IMGUIModule` no longer needed.
- **The synced upload webhooks are now clearly marked as not secret.** The config descriptions of the screenshot, death video, boss video and clip webhooks and the README say that every player can read them, and the server logs one line at start-up naming which of them are set. The design is unchanged: players' games post their own videos to Discord.
- The BepInExPack dependency is now 5.4.2351 (the latest on Hexium; BepInEx 5.4.23.5).

- **Weekly report tracking for the Players and Bosses tabs.**
  - **Bosses killed** per player is now counted by the server over the whole week (killing blows) instead of being worked out from the newest 50 boss kills in the window. The Players tab lists every player who did anything this week (joined, killed, died, dealt damage or killed a boss), not only those who joined, killed a mob or died.
  - **Per boss:** each recorded boss kill now also carries how many players *that boss* killed (the same player several times counts every time, only for the same boss object) and the highest single hit it received, with the player and the weapon. They are saved in the weekly data file (extended `boss` lines and new `bossfight` lines; older files still load, older builds ignore the extra fields). Deaths and hits of a boss that is fought but not yet killed are kept (also across the weekly cut-off, at most 14 days) and handed to the kill. Kall Fimbulbringer's three phases count as one boss.
  - **Network:** a death caused by a boss now also sends the boss's object key (the previously empty last field of the death event), and a new event kind `5` carries the best hit a boss received. An older server ignores both; an older game does not send them. The snapshot reply has an extension block appended after the original layout, so older games still read it and a new game copes with a reply from an older server.
  - **In-game Bosses tab:** new columns **Boss**, **Killing blow**, **Highest hit received** (damage, who landed it, weapon), **Kills** (players that boss killed) and **When**; one row per boss kill, so the same boss killed twice in a week is two rows. Every text cell wraps instead of being cut with "...". With a server of an older build the two new columns show "-".

- **Weekly report tracking for the Mobs killed tab.**
  - **Highest hit per kind of mob:** every hit a player lands on a mob (never a boss) is measured by the game that owns the mob, like the highest hit and the boss hits. Only the best hit per player and kind of mob is kept and sent, and only when it beats the one that game already sent, so a long fight costs at most one small message every five seconds. The server keeps the best hit of every player on every kind of mob all week (saved in the weekly data file as new `mobhit` lines; older files still load, older builds ignore the lines) and works out the best of everybody, and who landed it, when a snapshot is built. The mob name is worked out exactly like the one of a mob kill, so a mob's kills and its best hit always meet under the same English name. The damage is the health the hit removed, so the killing blow counts for what was left of the mob.
  - **Network:** new event kind `6` (`EventMobHit`, same layout as the highest hit: mob prefab, mob name, player, damage). The snapshot reply has a second extension block after the first one (version `2`: per player and combined, the best hit of the mobs in the top five). A game of an older build stops reading before it; a new game copes with a reply from an older server (the hit columns then show "-"). A server of an older build logs the new event as an unknown kind and ignores it.
  - **In-game Mobs killed tab (replaces the Mob rankings tab):** **Top 5 most mobs killed on the server** (columns **Name of mob**, **Amount**, **Highest hit received**, **By who**) and one **Top 5 most mobs killed by <player>** table per player (the same without By who). The name column is never narrower than its longest name needs and wraps a very long name onto a second line instead of cutting it with "..."; the other cells wrap too. The server table is shown first, so it is not buried under the player tables.
  - **In-game Killer mobs tab (split off the old Mob rankings tab):** **Top 5 mobs that killed players on the server** (columns **Name of mob**, **Amount**, **Most deaths by whom**) and one **Top 5 mobs that killed <player>** table per player (Name of mob, Amount). Same wrapping rules as the Mobs killed tab; players who died most are listed first. **Most deaths by whom** (the player who died to that mob most times, bosses included) is now worked out by the server (see below). The tabs are two rows of three: Players, Bosses, Mobs killed / Killer mobs, Death causes, Highest hits.

- **Weekly report tracking for the Killer mobs tab.**
  - **Most deaths by whom:** the server already counted, per player, how often each mob (bosses included) killed them and saved it in the weekly data file (`mobdeath` lines). It now also works out, for each mob in the combined top five, which player died to it most times, over ALL players of the week (not only the ones the window lists). Players tied for the most deaths are all named (alphabetically, at most three, then "+N more"), because with only a few deaths a tie is the usual case. The tab shows the name instead of "-".
  - **Network:** the snapshot reply has a third extension block after the other two (version `3`: per mob in the combined top five, the "who" text). A game of an older build stops reading before it; a new game copes with a reply from an older server (the column then shows "-"). No new events and no change to the weekly data file: older and newer builds read each other's files.
  - Build tag in the log is now `killer-mobs` (later builds of 1.3.5 change it).

- **Weekly report tracking for the Death causes tab.**
  - **Finishing hit:** when a player dies, their game now also reads the hit that killed them (the game's own last hit: its total damage and the damage type that makes up most of it: Blunt, Slash, Pierce, Chop, Pickaxe, Fire, Frost, Lightning, Poison or Spirit; plain damage such as falling or drowning has no type). The server keeps the biggest finishing hit per player and cause of death all week (saved in the weekly data file as new `deathhit` lines; older files still load, older builds ignore the lines) and works out the biggest of everybody when a snapshot is built.
  - **Highest amount by who:** the server works out, for each cause in the combined top five, which player died of it most times, over ALL players of the week. Ties are all named (alphabetically, at most three, then "+N more").
  - **Network:** new event kind `7` (`EventDeathHit`: damage type, cause of death exactly as the death event sent it, damage), sent right after the death event, and a fourth extension block at the end of the snapshot reply (version `4`: the finishing hit of the top causes per player and combined, and the "who" text). A game of an older build stops reading before it; a new game copes with a reply from an older server (the new columns then show "-"); a server of an older build logs the new event as an unknown kind and ignores it.
  - **In-game Death causes tab (replaces the plain ranking lists):** **Most common death causes on the server** (columns **Cause name**, **Finishing hit/damage**, **Amount**, **Highest amount by who**) and one **Most common death causes by player <name>** table per player (the same without the last column). Same wrapping rules as the Mobs killed and Killer mobs tabs: the name column is never narrower than its longest name needs, and every cell wraps instead of being cut with "...". The players who died most are listed first.
  - The finishing hit is the game's own record of the last hit when the player died (the mod's own record of the last hit is the fallback); a death for which neither exists shows "-".
  - Build tag in the log is now `death-causes-2`.
  - **Death causes tracking reworked (fixes wrong and missing causes).**
    - **The cause of a death without a killer now comes from the type of the killing hit** (new `DeathMessage.CauseName`, `DeathMessageBuilder.EnvironmentalCauseName`): Drowning, Burning, Freezing, Poison, Water damage, Smoke, Edge of the world, Cinder fire, Ashlands ocean, Lava, Incinerator, Fall damage, Self-inflicted damage, Falling tree, Cart, Boat, Collapsing structure, Falling stalactite, Drawbridge, Turret, Catapult and Impact damage. Before, the cause was guessed by searching the death *sentence* for words: it contained the player's own name and a random joke, so a player called "Waterman" drowned in every death, a falling tree was filed as "Fall damage", and lava, Ashlands ocean, carts, boats and collapsing structures all ended up as "Unknown / environment". The word search is now only the last resort, with the player's name taken out of the text.
    - **A creature has the same name on the Death causes and Killer mobs tabs.** The cause was named without the killer's prefab, so a game that could not give English text produced "Greydwarf Elite" on one tab and "Greydwarf Brute" on the other, and the same creature was counted twice under two names. The server now names the cause with the prefab, exactly like the mob death (the finishing-hit event carries the killer's prefab in its previously empty `killer` field; an older game sends it empty and behaves as before).
    - The game's own log (`BepInEx/cache/ValheimDiscordRelay.log`) now has one line for every death a game reports (`[Report] Death reported: ...`) and, on the server, one for every death and finishing hit it receives (`[Report] Death received ...`, `[Report] Finishing hit received ...`), so a missing row can be traced.

### Fixed (report review)
- **A killing blow was recorded as only the health the creature had left.** A 200 damage hit on a creature with 30 health left showed as 30 in the Highest hits tab, the Mobs killed tab's **Highest hit received** column and the boss's **Highest hit received**, so the best hit on a kind of mob could never be much higher than that mob's health. For a hit that kills, the full damage of the hit (the game's own total, after resistances and bonuses) now counts for these "highest hit" numbers. **Total Damage** still counts the health really removed. A hit that does not kill is measured exactly as before, and if the game's total cannot be read the old number is used.
- **Highest hits tab cut long names with "...".** Player, weapon and enemy names now wrap onto more lines (the row grows with them), like the other tabs.
- **The "Bosses killed" column of the Players tab was a few pixels too narrow for its own sort arrow** (the header is longer while that column is sorted); the column is 12 px wider.
- Build tag in the log is now `hit-size-1`.

### Fixed
- **Report window style sheet: `border-width: 1px` was rejected.** Plain-number properties (`border-width`, `border-left-width`, `opacity` ...) did not accept a `px` suffix, although the README lists `border-width` as supported; they now accept `1px` as well as `1`.
- **A solo boss kill did not escape the player's name for Discord markdown.** The "has fallen!" embed showed a name like `xX_Slayer_Xx` in italics, and a name containing `[text](url)` became a clickable link. The name is now escaped and capitalised the same way as in the party message (`Server/BossAnnouncer.cs`). Nobody could be pinged (`allowed_mentions` is empty).
- **A small `Server - Queue Limit` could drop parts of the weekly report.** The report is up to 24 messages queued at once, but the limit can be as low as 10, and busy chat could push report parts out as well; dropped parts were only sent again by the 15-minute retries. The report's messages (also the test report's) are now exempt: they do not count towards the limit and are never discarded by it. Chat and the other messages are limited as before (`Server/WebhookWorker.cs`, `ServerRelay.EnqueueJson`, `Report/ReportScheduler.cs`).
- **One player can no longer flood the Discord chat queue.** Each connected player may relay a burst of 10 chat lines and then 1 per second; extra lines are not sent and a warning is logged at most every 30 seconds per player. The host's own chat is not limited (`ServerRelay.AllowChatFrom`).
- **A failure while starting the server relay can no longer break the game's startup.** `GameStartPatch` now catches and logs it like the other patches.
- **Webhook messages are no longer lost on a network blip.** A timeout, DNS failure or dropped connection (no HTTP answer from Discord) is now retried with the normal backoff instead of dropping the message after one try (`Server/WebhookWorker.cs`).
- **Video uploads retry the same file instead of re-encoding.** A network error, HTTP 429 or HTTP 5xx on a death, boss or clip upload is retried up to 3 times with the same file; only HTTP 413 (too large) moves to a smaller encode, and other failures no longer waste lower-quality encodes. A timeout while waiting for Discord's answer is not retried, so a video that may already have been posted is not posted twice (`UploadWithRetry` in `Client/ClientCaptureService.Webhook.cs`).
- **A slow Discord could freeze the server for up to ~9 s at start and shutdown.** The up / restart / down notices were sent on the game's main thread and waited for. They now go through the normal send queue: starting never waits, and at shutdown the game waits at most 2 seconds for the queue (the "going down" notice included).
- **Messages still queued at shutdown were thrown away, and the "going down" notice could arrive before earlier ones.** The notice keeps its place in line, and when the plugin is unloaded the worker keeps sending for up to 4 more seconds instead of dropping the queue after 1.5 s.
- **The mod replaced the game's TLS setting on every Discord request**, overriding other mods. TLS 1.2 is now only added, once, if it is missing (new `Common/HttpSecurity.cs`).
- **A boss kill reported again after a server restart could be counted twice in the weekly report, and a boss announcement could be repeated after a restart.** Both are now remembered across restarts (see above).
- **The weekly report's statistics were reset as soon as the report was queued, so a report Discord refused, or that could not be delivered, lost the week.** They are now reset only after Discord has confirmed it; otherwise the numbers are kept and the report is tried again after 15 minutes (4 attempts, then the period is closed with an error in the log). The confirmation is written to disk at once, so a server that stops right after Discord accepted the report no longer posts it a second time after the restart.
- **The weekly report never rolled over without a webhook.** With `[Weekly Report] Server - Webhook URL` empty, the period now ends when the report is due (with a warning in the log) instead of growing for ever.
- **Damage and kills waiting to be sent were lost when there was no connection to the server.** The batch is kept and sent at the next flush, and dropped when a new connection begins.
- **A player name with characters a path cannot hold stopped all later death videos.** The file on disk now has a fixed name (`death-HHmmss.webp`), the name Discord shows is the player name with unusable characters replaced by `_` (at most 40 characters), and an unexpected error on the processing thread now clears the "processing" flag and sends the text-only death message.
- **The screenshot hotkey fired while typing** in the chat, the console or a sign / map-pin text box.
- **A player name starting with `#`, `-` or `1.` could turn the join / leave message into a heading, bullet or numbered list in Discord.** These are now escaped.

## 1.3.4
- **Death camera.** After you die, the camera rises and looks down at your body, turning towards the killer if there is one, so the death video ends on a cinematic shot.
- **Death camera settings.** New `[Player Deaths]` settings: `Client - Death Camera`, `Client - Death Camera Height` and `Client - Death Camera Look At Killer`.
- **Screenshot hotkey works while other keys are held.** Moving or holding Shift no longer blocks it.
- **Black screenshots are retried automatically** with a different capture strategy. A new native strategy is tried first.
- **Last-resort fallback.** If every capture is black, the newest death-video frame is sent instead (file name ends in `-buffered`).
- **The working screenshot strategy is remembered** between game sessions, per PC and graphics setup.
- **Smoother screenshots.** The capture texture is reused between screenshots and no longer shared with the death-video capture.
- **More screenshot diagnostics.** Each screenshot logs the game state, so a black screen can be told apart from a failed capture.
- **Build tag in the log.** The startup line shows a build tag and the DLL write time.

## 1.3.3
- **The in-game death shout is now sent 5 seconds after the death**
- **Faster, multithreaded WebP encoding that stays light on the PC.** 
- **Quitting the game while a video is processing no longer hangs it.** 
- **Config reorganised by feature, with `Server - ` / `Client - ` names.** 
- **Every feature has its own Enabled switch.**
- **Repeat-death message changed.** A second death while the first is still being recorded/uploaded (or within 30 s of it) now posts three lines in one code block: the normal random death quote for that death, `{Player} died again in a very short time, maybe fight an enemy of your size: T.W.I.G.`, and `No video recording of this unfortunate death will be presented.` It replaces the old "seems to be weak" sentence.
- **Death video and boss video are now independent of the frame recording.** 
- **New `[Admin]` config category** holding `Lock Configuration` and `Log Successful Sends` (both synced, admin-controlled).
- **Config category `[Server]` renamed to `[Chat]`.**
- Introduced **Boss death video recording**, which by default uses same recording and FPS settings set for Death recording. 
- **Admin-set webhooks and report schedule.** The webhook URLs the clients upload to are now synced from the server: the admin sets them once on the server and every client receives them.
- **Player notifications:** new `[Player Notifications]` section and its own webhook which notifies when players join or leave the server. Works on local hosted worlds as well.
- **Server notifications** are now sent with the username "Server notifications". New synced checkbox `[Server Notifications] Use Discord Webhook Name` sends no username so Discord shows the name set on the webhook. The server name is bold in every message.
- **Weekly report (first version):** new `[Weekly Report]` section, its own webhook. One embed per week with: players joined, bosses defeated, highest damage dealt, and a table of mob kills and deaths per player. Statistics are saved to `BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt`. `Send Test Report` posts a preview without resetting anything.
- **Death quotes are randomly selected from a list based on different criteria**.
- **Fixed chat from the host not reaching Discord on a world hosted from Valheim's own menu (no dedicated server)**. 
- **Fixed the server always reporting "restarted" after any clean shutdown**, however long it had been off. A startup is now a restart only if it happens within `Restart Window Seconds` (new config, default 120) of the shutdown; otherwise it is reported as "is up".
- **Fixed name display for mini-bosses.** Enemy names carrying a `<color=...>` tag are shown in Discord in red ANSI colour instead of raw tags (e.g. Geirrhafa). In-game chat shows the plain name only, with no tags.

## 1.3.2
- Added server up/down/restart notifications.
- Intercept Valheim's own arrival message.
- Forward it to the Server Notifications webhook.
- Prevent duplicate arrival notifications.
- Deaths now shout in game chat.
- Death shouts stay out of the normal Discord webhook.

## 1.3.1
- Rolling pre/post-death frame buffer now lives in memory instead of on disk; disk is only touched once a death happens, to stage frames for `img2webp`.
- Frame staging and the WebP encode/upload now run entirely on a worker thread instead of partly on the main thread.
- Fixed logging out (or quitting to desktop) being misreported as a death, sending an unwanted death message/video. The local player object is destroyed on logout/quit the same way it is on death; death detection now checks `Game.IsShuttingDown()` and skips the death signal whenever the game itself is the one tearing the session down.

## 1.3.0
Performance rework for the frame-rate drop reported while the death-video rolling buffer and manual screenshots were active.

- Death-video capture now downscales on the GPU before reading back, instead of reading back full resolution twice.
- Screen readback uses `AsyncGPUReadback` (non-blocking), with a synchronous fallback when unsupported.
- GPU downscale now preserves aspect ratio (letterboxed) instead of stretching.
- Manual screenshots use the same GPU-side capture path, removing a stutter on every keypress.
- Disk writes, uploads, and frame staging now run on background threads instead of the main thread.
- All logging now goes through one file (`ValheimDiscordRelay.log`) via a single background thread; the separate death log is gone.
- Log lines now use one consistent format and always match their actual severity.
- Old-file cleanup now runs at most every 5 minutes on a background thread, instead of every frame.
- Building from source now requires `UnityEngine.ScreenCaptureModule.dll` in `References/` (no new runtime dependency for end users).

## 1.2.2
- Added client-side death videos as animated WebP, 2-4s before/after death, 20/30 FPS, 480x270/640x360/960x540.
- Death videos upload directly from the client to the configured webhook.
- Added automatic encoding fallbacks; if all WebP attempts fail, only the death message is sent.
- Death frames are disk-backed instead of a large RAM buffer.
- Screenshots and death-video files are cleaned up automatically after upload or expiry.
- All logging (client and server) now goes through `ValheimDiscordRelay.log` first, with BepInEx console output as a secondary, correctly-leveled step.

## 1.2.1
- Adjusted `README.nfo`.

## 1.2.0
- Consolidated client and server into one `ValheimDiscordRelay.dll` with one shared config file.
- Server component stays inactive on clients.
- Screenshot hotkey defaults to `PageUp`; screenshots use PNG.
- Added Thunderstore/Hexium publishing metadata and icon.

## Death-cause statistics
- Added per-player and combined weekly death-cause counts.
- Death cause rankings are exposed in the Home report window's Death causes tab and the Discord weekly report.
- Cause labels use the identified attacker when available; otherwise they classify common environmental causes from the existing death message, falling back to Unknown / environment.
- Existing weekly report data is extended with death-cause records; older saved data remains loadable and starts these counts at zero.
