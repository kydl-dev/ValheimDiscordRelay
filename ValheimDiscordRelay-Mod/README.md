# Valheim Discord Relay

One BepInEx plugin for Valheim that connects your world to Discord through webhooks. It relays chat, announces deaths, boss kills, players joining and leaving and server restarts, posts a weekly statistics report, and lets players send screenshots and short video clips with one key press.

The plugin is a single DLL. It contains a server part and a client part; each side activates only where it is needed.

## Contents

- [What it can do](#what-it-can-do)
- [Requirements](#requirements)
- [Installation and updating](#installation-and-updating)
- [Quick start](#quick-start)
- [How the pieces fit together](#how-the-pieces-fit-together)
- [Configuration](#configuration)
- [Features in detail](#features-in-detail)
  - [Chat relay](#chat-relay)
  - [Manual screenshots](#manual-screenshots)
  - [Clips](#clips)
  - [Death messages](#death-messages)
  - [Death videos and the death camera](#death-videos-and-the-death-camera)
  - [Boss death announcements](#boss-death-announcements)
  - [Server notifications](#server-notifications)
  - [Player notifications](#player-notifications)
  - [Weekly report (Discord)](#weekly-report-discord)
  - [In-game weekly report window](#in-game-weekly-report-window)
  - [Version check](#version-check)
  - [Delivery, queueing and retries](#delivery-queueing-and-retries)
- [Files the mod creates](#files-the-mod-creates)
- [Security and privacy](#security-and-privacy)
- [Performance](#performance)
- [Logs and troubleshooting](#logs-and-troubleshooting)
- [Building from source](#building-from-source)
- [Credits and licence](#credits-and-licence)

## What it can do

| Feature | In one line |
| --- | --- |
| **Chat relay** | Normal chat and shouts go to Discord, each with its own webhook, shown under the player's name. |
| **Death messages** | A randomly chosen, cause-specific message is shouted in game and posted to Discord when a player dies. |
| **Death videos** | A short animated WebP of the player's death (seconds before and after) is posted with the message, ending on a cinematic death camera. |
| **Boss announcements** | An embed with the boss's portrait and the top damage dealer, plus a short video of the boss dying. |
| **Clips** | Press a key and the last 4 and next 4 seconds are posted as a short video. |
| **Screenshots** | Press a key and the current game screen is posted, with automatic black-screen protection. |
| **Server notifications** | Server is up / restarted / going down. |
| **Player notifications** | "Has joined the world" / "had to leave us", with the current population. |
| **Weekly report** | Once a week, one Discord message per tab: players, bosses, mobs killed, killer mobs, death causes and highest hits, with a top 5 for the server and for every player. |
| **In-game report window** | Press a key to see the same numbers as they stand right now, in a resizable, sortable, CSS-styleable window. |
| **Version check** | The server can refuse players whose copy of the mod is missing or is not exactly the server's version. |
| **Admin controls** | Webhooks and settings are set once on the server and synced to players; settings can be locked to admins. Every feature has its own switch. |

The mod has no chat or console commands; everything is configuration, hotkeys and automatic events.

## Requirements

- Valheim 1.0.12
- BepInEx 5.4.2351 (BepInEx 5.4.23.5)
- [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/) and **KydlCore** (the KydlDev shared runtime: the KY hub window, the Settings tab and the shared log). Both are installed with the mod by a mod manager, and are needed on the dedicated server as well.
- Install the mod on the dedicated server **and on every player's game, all with exactly the same version.** The server refuses players who have no mod, an older version or a newer version (see [Version check](#version-check)).
- Worlds hosted from Valheim's own menu (no dedicated server) work too: install the mod on the host and set the webhook URLs in the host's config. The same version check applies to the players who join the host.
- Death videos, boss videos and clips are encoded with a bundled **Windows program** (libwebp's `img2webp.exe`). A game that cannot run it still sends the death and boss messages, without the video; a clip then fails with a notice in game.

## Installation and updating

Install the Thunderstore/Hexium package, or place `ValheimDiscordRelay.dll` in `BepInEx/plugins/ValheimDiscordRelay/`. Do this on the server and on every client.

**Updating from 1.x:** version 2.0.0 is one plugin with a new id (`com.kydldev.ValheimDiscordRelay`) and one new config file, `BepInEx/config/com.kydldev.ValheimDiscordRelay.cfg`. The old `ValheimDiscordRelay.cfg` is no longer read: remove it, start the game once so the new file is written, then fill in your webhooks again (or paste them into the KY hub, see below). Each section's `Enable` switch is now simply `Enable`, and the `[Admin]` section is now `[General]`. Remove the old 1.x plugin file so only one copy loads.

## Quick start

1. In Discord, create a webhook for each feature you want (Channel settings → Integrations → Webhooks). You may use one channel for everything, but see [Security and privacy](#security-and-privacy) for the webhooks that players' games can read.
2. Start the dedicated server once with the mod installed so `BepInEx/config/com.kydldev.ValheimDiscordRelay.cfg` is created.
3. Stop the server, open the file and paste each webhook URL into its `Server - Webhook URL` setting (see [what goes where](#how-the-pieces-fit-together)). A feature whose webhook is empty sends nothing.
4. Start the server. Players who join with the same mod version receive the shared webhooks and settings automatically; they do not enter webhooks themselves.
5. Optional: set `[General] Server - Log Successful Sends = true` for a while to see every delivery in the log.

Everything is enabled by default; with no webhook set, a feature simply logs a warning and does nothing.

## How the pieces fit together

Every feature that sends something to Discord has its own config category, its own webhook setting and its own `Enabled` switch, so you can run only the parts you want and point each at its own channel.

| Feature | Category | Sent by | Webhook setting | Sent as |
| --- | --- | --- | --- | --- |
| Normal chat | `[Chat]` | server | `Server - Normal Webhook URL` (falls back to `Server - Webhook URL`) | the player's name |
| Shouts | `[Chat]` | server | `Server - Shout Webhook URL` (falls back to `Server - Webhook URL`) | the player's name |
| Manual screenshots | `[Screenshots]` | player's game | `Server - Webhook URL` | the player's name (optional) |
| Clips | `[Clips]` | player's game | `Server - Webhook URL` | `ValheimRelay Clips` |
| Death message + video | `[Player Deaths]` | player's game | `Server - Webhook URL` | the player's name |
| Boss announcement | `[Boss Death]` | server (embed) + a player's game (video) | `Server - Webhook URL` | `Server - Webhook Username` |
| Server up / down / restart | `[Server Notifications]` | server | `Server - Webhook URL` | `Server notifications` (or the webhook's own name) |
| Player joined / left | `[Player Notifications]` | server | `Server - Webhook URL` | `Player notifications` |
| Weekly report | `[Weekly Report]` | server | `Server - Webhook URL` | `Weekly report` |

Who measures what:

- **Chat, join/leave, server notices and the weekly report** are posted by the server.
- **Screenshots, clips, death videos and boss videos** are uploaded **straight from the player's game** to Discord; the files never pass through the server.
- **Damage, kills and boss fights** are measured by the game that *owns* the creature (Valheim only applies damage on the owner), so that player needs the mod. Deaths are reported by the dying player's own game. The server adds everything up.

## Configuration

Everything is in one file: `BepInEx/config/com.kydldev.ValheimDiscordRelay.cfg`. Every entry is also on the **Settings tab of this mod in the KY hub (F6)** and in Configuration Manager (F1). Admins can change the server's values there while connected, and the change is saved on the server. BepInEx writes a description above every setting.

Setting names tell you who they belong to:

- **`Server - ...`** — set by the server admin. Either **synced** to every player, or **server only** (used by the server and never sent anywhere).
- **`Client - ...`** — each player's own setting, never synced.

Every category has an `Enable` switch (admin, affects everyone) and most have a `Client - Enabled` switch (the player opting out). **A feature that has both runs only when both are on.** `[General] EnableAll` (on by default) is the master switch: while it is off the whole mod is inert on that machine, and the same is true of a category whose `Enable` is off.

### What is synced

The mod uses ServerSync for the shared settings and a small admin-only channel for the server-only ones. Players do not enter webhooks; the admin sets them in the **server's** config, in the file or live in the KY hub (F6).

- Webhooks the **players' games upload to** (screenshots, clips, death videos and messages, boss videos) are synced to every client. This is needed because each game posts its own file straight to Discord. It means **these URLs are not secret**; see [Security and privacy](#security-and-privacy).
- Webhooks **only the server uses** (chat, server notifications, player notifications, weekly report) stay on the server and are never sent to ordinary players. An admin's game fetches them over a separate admin-only channel (the server checks `adminlist.txt` on every request), so the admin can read and change them in the KY hub; nobody else receives them.
- Settings marked *synced* below are sent to players and can only be changed by admins while `[General] Server - Lock Configuration` is on; other players see them locked in the KY hub.

### `[General]`

| Setting | Default | |
| --- | --- | --- |
| `EnableAll` | `true` | Master switch of this mod on this machine. Off = nothing is relayed, captured or measured. |
| `Server - Lock Configuration` | `true` | Synced. Only players in the server's `adminlist.txt` can change synced settings from their game. If `false`, **any** connected player can change them, webhook URLs included. Leave it on for a public server. |
| `Server - Require Same Version` | `true` | Server only. The server refuses players who do not have exactly the server's version of this mod, including players without it. See [Version check](#version-check). |
| `Server - Log Successful Sends` | `false` | Synced. Log every successful Discord delivery (the server logs its own, each player's game logs its own uploads). |

### `[Chat]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Enable the chat relay. |
| `Server - Webhook URL` | empty | Server only. Fallback, used when the Normal / Shout webhook is empty. |
| `Server - Normal Webhook URL`, `Server - Shout Webhook URL` | empty | Server only. Webhooks for normal chat and for shouts. |
| `Server - Max Message Length` | `1800` | Synced. Longest text sent to Discord (100–1900). |
| `Server - Name Display` | `NameOnly` | Synced. `NameOnly` (`Bjorn`), `NameWithNumber` (`Bjorn [1]`, for players with the same name) or `NameWithIdSuffix` (`Bjorn [5678]`, last 4 characters of the platform ID). |
| `Server - Shout Prefix`, `Server - Normal Prefix` | dim red / dim cyan | Synced. ANSI colour codes placed before a shout / normal chat. Leave them alone unless you want other colours. |
| `Server - Queue Limit` | `100` | Synced. Most queued messages; the oldest are dropped when full (10–1000). The weekly report's messages are not counted and are never dropped by this limit. |
| `Server - Minimum Send Interval Ms` | `250` | Synced. Pause between webhook requests (50–5000), to stay under Discord's rate limits. |
| `Client - Enabled` | `true` | Send the chat typed in this game to the server. Turn off to keep your chat out of Discord. |
| `Client - Max Message Length` | `1000` | Most characters sent from this game (1–1000). The server applies its own limit too. |

### `[Screenshots]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Turn off to disable screenshots for every player. |
| `Server - Webhook URL` | empty | Synced. The GLOBAL screenshot webhook: normal screenshots go here, and so do the other two kinds when their own webhook is empty. |
| `Server - HUD Hidden Webhook URL` | empty | Synced. Webhook of the screenshots without HUD. Empty = the global webhook. |
| `Server - HUD And Player Hidden Webhook URL` | empty | Synced. Webhook of the screenshots without HUD and without the player. Empty = the global webhook. |
| `Client - Enabled` | `true` | Allow screenshots in this game: off = none of the three kinds works. |
| `Client - Normal Enabled` | `true` | Allow the normal screenshot. Off = its key does nothing and its F6 button is hidden. |
| `Client - HUD Hidden Enabled` | `true` | Allow the screenshot without HUD (same). |
| `Client - HUD And Player Hidden Enabled` | `true` | Allow the screenshot without HUD and player (same). |
| `Client - Key` | `PageUp` | Normal screenshot (HUD on). `None` disables it. Works while other keys (walking, Shift) are held. |
| `Client - HUD Hidden Key` | `P` | Screenshot without HUD (like Ctrl+F3). `None` disables it. Not while typing. |
| `Client - HUD And Player Hidden Key` | `Alt + P` | Screenshot without HUD and without your own character. `None` disables it. Not while typing. |
| `Client - Include Player Name` | `true` | Use your character name as the Discord sender (otherwise `Valheim Player`). |

### `[Clips]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Turn off to disable clips for every player. |
| `Server - Webhook URL` | empty | Synced. The clips webhook. |
| `Client - Enabled` | `true` | Allow the clip key in this game. |
| `Client - Key` | `PageDown` | `None` disables it. For two keys together write both, e.g. `F9 + LeftControl`: hold both, in any order; other keys held at the same time are ignored. |
| `Client - Include Player Name` | `true` | Add the line `<name> saved a clip.` to the message that carries the clip. The Discord sender is always `ValheimRelay Clips`. |
| `Client - Pre Duration`, `Client - Post Duration` | `4`, `4` | Seconds before / after the key press (2–4 each). |

Clips use the frame rate and resolution of `[Player Deaths] Client - Video FPS` and `Client - Video Resolution`.

### `[Player Deaths]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Turn off to disable death messages, shouts and videos for every player. |
| `Server - Webhook URL` | empty | Synced. Webhook for death messages and videos. |
| `Client - Enabled` | `true` | Announce this player's deaths (in-game shout and Discord message). |
| `Client - Video Enabled` | `true` | Record a death video. When off, the message is still sent, without a video. It also keeps the frame buffer that boss videos and clips reuse. |
| `Client - Video FPS` | `30` | `20` or `30`. Use `20` if your game runs slower than 30 FPS. |
| `Client - Video Resolution` | `960x540` | `480x270`, `640x360` or `960x540`. |
| `Client - Video Pre Duration`, `Client - Video Post Duration` | `4`, `4` | Seconds before / after the death (2–4 each). |
| `Client - Death Camera` | `true` | After you die, raise the camera and look down at your body. It also moves your own view for those seconds. |
| `Client - Death Camera Height` | `6` | Metres the camera rises (1–15). |
| `Client - Death Camera Look At Killer` | `true` | Turn the view towards whatever killed you, without losing your body from view. |

### `[Boss Death]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Announce boss deaths. |
| `Server - Webhook URL` | empty | Synced. Used for both the embed and the video. |
| `Server - Webhook Username` | `Boss Death Announcement!` | Synced. Sender name of the embed and the video. |
| `Server - Video Source` | `Boss Owner` | Synced. Whose game records the boss video: `Boss Owner` or `Last Attacker`. |
| `Client - Enabled` | `true` | Take part in boss announcements from this game (damage measuring and video). |
| `Client - Video Enabled` | `true` | Upload the boss video from this game. |
| `Client - Video FPS`, `Client - Video Resolution` | `30`, `960x540` | Only used when `[Player Deaths] Client - Video Enabled` is off; otherwise the boss video reuses the death video's frames. |
| `Client - Video Pre Duration`, `Client - Video Post Duration` | `4`, `4` | Seconds before / after the boss dies (2–4 each). |

### `[Server Notifications]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Admin only. Enable the feature. |
| `Server - Webhook URL` | empty | Server only. |
| `Server - Use Discord Webhook Name` | `false` | Synced. Off: messages are sent as **Server notifications**. On: Discord shows the name set on the webhook itself. |
| `Server - Up Notification`, `Server - Down Notification`, `Server - Restart Notification` | `true` | Server only. Turn each message on or off. |
| `Server - Restart Window Seconds` | `120` | Server only. A startup within this many seconds of a clean shutdown counts as a restart (10–3600). |

### `[Player Notifications]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. On: the mod posts its own join / leave messages and Valheim's "I have arrived!" shout is not relayed. Off: nothing is posted here, and the arrival shout is relayed as a normal shout. |
| `Server - Webhook URL` | empty | Server only. |
| `Server - Arrival Avatar URL`, `Server - Leave Avatar URL` | empty | Synced. Optional `http(s)` image URLs used as the webhook icon for join / leave messages. Anything else is ignored with one warning. |

### `[Weekly Report]`

| Setting | Default | |
| --- | --- | --- |
| `Enable` | `true` | Synced. Collect the statistics and post the report. Turning it off also stops games from collecting. |
| `Server - Webhook URL` | empty | Server only. |
| `Server - Report Day` | `Sunday` | Synced. Day the report is posted. |
| `Server - Report Time` | `20:00` | Synced. `HH:mm`, in the **server's** time zone. An invalid value falls back to `20:00` with a warning. |
| `Server - Send Test Report` | `false` | Synced. Set to `true` to post a "(test)" report right now. It resets itself to `false` and does not clear the statistics. It also fires once at server start if it was left `true`. |
| `Server - Window Enabled` | `true` | Synced. Let players open the in-game report window. Does not affect the Discord report or what is collected. |
| `Client - Enabled` | `true` | Include this player in the report (this game measures your kills and damage and reports your deaths). |
| `Client - Window Key` | `Home` | Opens and closes the window; `None` disables it. Two keys together are allowed, e.g. `F8 + LeftControl`. |
| `Client - Window Style` | `Classic` | Look of the window: `Classic` (the original flat look) or `Modern` (rounded cards, pill tabs, gold primary button, key hints, Valheim's rune font for the title). Only the look changes. Applies the next time the window opens. |
| `Client - Window Scale` | `0` | `0` follows your screen height (normal size at 1080p, larger on taller screens), or `0.7`–`2.0` to set it yourself. |
| `Client - Window Width`, `Client - Window Height` | `0`, `0` | The size you gave the window by dragging its bottom-right corner. Saved automatically, so there is normally no need to edit them. `0` = the default size. |

## Features in detail

### Chat relay

The player's game sends only the chat type and the text; the **server works out the sender** from the network connection. No Discord account linking is used.

- **Normal chat and shouts** are relayed; **whispers never are.** Normal chat is shown in dim cyan and shouts in dim red (ANSI colours inside a code block).
- The Discord sender name is the character name, in the format chosen by `Server - Name Display`. `@everyone` / `@here` in names are defused, and **no message the mod sends can ping anyone.**
- A triple backtick in a player's text cannot break out of the code block, and text longer than `Server - Max Message Length` is cut with `…`.
- **Spam protection:** each connected player may relay a burst of 10 lines and then about 1 per second. Extra lines are not sent and a warning is logged at most every 30 seconds per player. The host's own chat on a menu-hosted world is not limited.
- **Menu-hosted worlds:** the host is not a network peer, so the host's chat is handed to the relay directly. It is shown under the host's character name.
- **Valheim's own "I have arrived!" shout** is dropped while `[Player Notifications]` is enabled (the mod posts its own join message instead) and relayed like any other shout when it is off.
- Death shouts the mod posts in game chat are never relayed to the chat webhook.

### Manual screenshots

Press `PageUp` (default) to capture the final rendered frame, including UI added by other mods. The PNG (`valheim-screenshot-<date>-<time>.png`) is uploaded straight from the client under your character name; there is no message text. The normal screenshot key works at any time.

**Screenshots without HUD.** `P` hides every HUD element the way Ctrl+F3 does, takes the screenshot and shows the HUD again (a HUD you had hidden yourself stays hidden). `Alt` + `P` also hides your own character for the moment of the capture. The files end in `-nohud` and `-nohud-noplayer`. Valheim does not use `P` or `Alt` + `P` itself; change them under `[Screenshots]` if another mod does. These two keys do nothing while you are typing in the chat, the console, or a sign / map-pin text box. The death-video frame fallback is not used for them (those frames show the HUD).

**F6 hub.** The mod's **Screenshots** tab has one button per kind; a button closes the window and takes that screenshot like its key. A kind you switched off, or that has no webhook, has no button. Each kind goes to its own webhook when one is set, otherwise to the global `[Screenshots] Server - Webhook URL`.

**Black-screenshot protection.** Every screenshot is checked for brightness. If it comes back black, other capture strategies are tried automatically, and as a last resort the newest frame from the death-video buffer is sent (needs death videos on; the file name ends in `-buffered`). If every attempt is black, the last frame is sent anyway, since the screen may genuinely be black. The strategy that worked is remembered for this PC and graphics setup in `BepInEx/cache/ValheimDiscordRelay.ScreenshotStrategy.txt`; delete the file to detect it again. Each screenshot logs the game state (player present, loading, teleporting, and so on), which shows whether a black result was a capture problem or a genuinely black screen.

### Clips

Press the clip key (`PageDown` by default) to save a moment: the 4 seconds before the key press and the 4 seconds after it (adjustable, 2–4 s each). Once the "after" part has been recorded the video is encoded as an animated WebP and uploaded straight from your game as **ValheimRelay Clips**, with the line `<your name> saved a clip.` A short notice at the top left of the screen says when it was sent (or that it failed). The notice appears only when the clip is finished, so it is never part of the video.

**Where the footage comes from.** A clip does not record anything itself: it is cut from the in-memory frame buffer the death video uses, which holds only the last 4 seconds (4.1 s with a small margin). That is why *Pre Duration* cannot be more than 4 seconds. The buffer runs while death videos are on, and also while clips are ready (`[Clips]` enabled on both sides, a key set and a webhook configured), so **clips work with death videos switched off**. While nothing is configured, nothing is captured.

- One clip at a time: a key press while the previous clip is still being recorded or sent is ignored (and logged).
- A death or boss video at the same moment is not affected; they share the same frames.
- If a game runs slower than the video frame rate, fewer frames are captured and the clip plays back faster than real time (the same applies to death and boss videos). Use `20` FPS in `[Player Deaths]` if that happens.
- If Discord refuses the sender name with HTTP 400, the clip is sent again once as `Valheim Clips` and that name is used from then on. The log says when this happens.
- Quitting the game cancels a clip in progress.

### Death messages

When the local player dies, the game works out what killed them from Valheim's hit data, picks a message for that kind of death and:

1. **shouts it in game chat** 5 seconds after the death (so it never ends up in the video), and
2. **sends it to Discord** through `[Player Deaths] Server - Webhook URL`, in a code block, together with the death video.

Both need `[Player Deaths] Enable` and `Client - Enabled` on. Without a webhook the shout still happens and the log says why nothing went to Discord. Logging out or quitting is never counted as a death.

**Which message.**

- **Animals and other enemies** get a random message from preset lists, chosen by kind of creature and star count (animals: no star, one star, two or more). The same message is never used twice in a row.
- **Bosses and special enemies** (names with a colour tag, such as Geirrhafa) get one fixed message, shown in red in Discord.
- **Environmental deaths** (drowning, burning, freezing, poison, smoke, lava, falling, the edge of the world and so on) get a random message that names the cause.
- **Crushing deaths** (falling tree, cart, boat, collapsing structure, stalagmite, drawbridge, turret, catapult, impact) get one fixed sentence each.
- **Unknown causes** use Valheim's own death text, or `got killed by Unknown.`

**Who gets the blame.** The attacker is remembered when it hits you, so it is still named correctly if it dies first (the snapshot is trusted for up to 60 seconds, 10 seconds when there is no hit data at all). Burning and poison deaths are credited to the creature that hit you in the previous 5 seconds. A death caused by another player names that player. Creature level is shown as stars: emoji in Discord, `(2 stars)` in game chat.

**Repeat deaths.** A second death while the first is still being recorded (or within 30 seconds of it) gets no new video. Discord gets the normal death message, a "died again in a very short time" line and a "no video recording" line, in one code block.

**Quitting mid-upload.** If you quit the game while a death video is still being prepared, the encoder is stopped and the death message is sent at once with a line saying you quit before the video finished.

All death quotes are in `Client/DeathQuotes.cs` and the boss/crushing sentences in `Client/DeathMessageBuilder.cs`; edit them there if you build the mod yourself.

### Death videos and the death camera

When the local player dies, the game records the configured time before and after the death and uploads it as an animated WebP, directly from the client, together with the death message. The attachment is named `<player>-death-<time>.webp`.

- **In memory, not on disk.** Frames are captured on the GPU at the chosen resolution and FPS and kept as JPEGs in memory for the last ~4 seconds. Disk is used only when a death happens, to stage frames for the encoder, and leftovers are cleaned up automatically (anything older than 24 hours is swept every few minutes).
- **Quality is chosen automatically.** The video is encoded at decreasing quality (80, 65, 50, 35) until it fits Discord's limit (the file must stay under 20 MiB, and an HTTP 413 from Discord also triggers a smaller encode). If every attempt fails, only the death message is sent.
- **Light on your PC.** Encoding runs as a separate low-priority process, split into parallel chunks, using at most a quarter of your logical CPU cores (at least one). One encode has a two-minute timeout.
- **Reliable uploads.** A temporary failure (HTTP 429 with Discord's `Retry-After`, HTTP 5xx, or a connection that never reached Discord) is retried with the *same* file, up to 3 attempts, instead of re-encoding. A timeout while waiting for Discord's answer is not retried, because the file may already have been posted.
- **Death camera.** Valheim leaves the camera where it was. With `Client - Death Camera` on, the camera instead rises smoothly while looking down at your body, and with `Client - Death Camera Look At Killer` on it also turns towards whatever killed you, without losing your body from view (a killer further than 60 m is ignored). It stops short of ceilings and branches, only runs for deaths that are being recorded, and moves your own view as well as the video.

### Boss death announcements

When a boss dies, the server posts an embed to the boss webhook, and a player's game uploads a short video of the moment as a separate message.

**The embed** has the boss's portrait (as webhook icon, thumbnail and large picture), the title `{Boss} has fallen!`, and a random party or solo quote naming the top damage dealer and their damage. With no recorded damage it says `{Boss} has been defeated.` Player names are escaped so they cannot restyle the message. The quotes are in `Server/BossQuotes.cs`.

**Damage** is measured by the game that owns the boss (so that player needs the mod) and sent to the server in small batches. The server adds it up per boss, so the totals survive the boss changing owner mid-fight. Fights nobody finished are forgotten after 3 hours.

**The video** is recorded and uploaded by one game, chosen by `Server - Video Source`:

- `Boss Owner` (default): the game that owns the boss when it dies.
- `Last Attacker`: the player who landed the last hit, who is almost always looking at the boss. **That player's own settings apply** (`[Player Deaths] Client - Video FPS` / `Resolution`, and `[Player Deaths] Client - Video Enabled` must be on, plus their own `[Boss Death]` client settings). If that game cannot record (mod missing, video off, busy, no answer), the boss owner's video is used. With a dedicated server that owns the boss, this is the only way to get a video.

When a video is expected, the embed waits for it so both appear together in Discord. If the video is not ready within 75 seconds (or the game quits), the embed is sent anyway.

**Which bosses.** Eikthyr, The Elder, Bonemass, Moder, Yagluth, The Queen, Fader and Kall Fimbulbringer are recognised by prefab name, in any language. Any other boss is announced with its in-game name and without images. Kall Fimbulbringer fights in phases and is announced once, on the final phase, with the damage of all phases added up.

**Announced once.** The server remembers the last 64 boss deaths it announced (in `BepInEx/cache/ValheimDiscordRelay.BossAnnounced.txt`), so a death reported again, for example right after a restart, is not announced twice. Entries older than 24 hours are not loaded again, so restoring an older world backup does not silence a boss that is killed again. Delete the file to forget them all.

**Images.** The portraits are hosted on `valheim.weirdgloop.org` and `valheim.tools`; the large picture is routed through the `wsrv.nl` image proxy to centre it. **Discord's servers fetch these images**, not your server. If one of those sites is down, the embed is still posted, just without that picture.

### Server notifications

`[Server Notifications]` posts when the server starts, stops or restarts. The server name is bold in every message:

```text
Server **My World** is up.
Server **My World** restarted.
Server **My World** is going down.
```

A startup within `Server - Restart Window Seconds` of a clean shutdown is a restart; a longer gap is "is up". (The clean shutdown is recorded in `BepInEx/cache/ValheimDiscordRelay.clean-shutdown`.) Messages go through the same queue as everything else, so they keep their order and a slow Discord never holds up the server starting. At shutdown the game waits at most 2 seconds for the queue (including "is going down") to reach Discord; whatever is still unsent is retried for up to 4 more seconds while the plugin is unloaded, then dropped.

### Player notifications

`[Player Notifications]` posts when someone joins or leaves, sent as **Player notifications** with optional join / leave icons:

```text
{Player.name} has joined the world. Population: {players.count}. Good luck!
{Player.name} had to leave us. Hope they had fun. Population: {players.count}.
```

Valheim sends a message every time a character spawns (also after every respawn) and none when a player leaves, so the server watches the **connected players** instead: exactly one join per connection and one leave per disconnect, announced the moment the connection ends. Nothing is announced on respawn or when the server itself shuts down. On a menu-hosted world the host counts towards the population. Names are stripped of rich-text tags and escaped for Discord markdown (a name starting with `#`, `-` or `1.` cannot become a heading or list).

Joins are also counted for the weekly report, whether or not the Discord messages are enabled.

### Weekly report (Discord)

`[Weekly Report]` posts the report once a week through its own webhook, as **Weekly report**. The report is **one Discord message per tab** of the in-game window, each with its own colour, in this order:

| Tab | What it shows |
| --- | --- |
| **Players** | Players joined, bosses defeated, highest damage dealt, and a table of every player: Mobs killed, Bosses killed, Deaths, Total damage. |
| **Bosses** | One block per boss kill (killed twice = two blocks): Killing blow, Highest hit received (damage, player, weapon), Kills (players that boss killed before it fell) and When (shown in each reader's own time zone). |
| **Mobs killed** | *Top 5 most mobs killed on the server* (Name of mob, Amount, Highest hit received, By who), then *Top 5 most mobs killed by player* for every player. |
| **Killer mobs** | *Top 5 mobs that killed players on the server* (bosses included; adds Most deaths by whom), then the same per player. |
| **Death causes** | *Most common death causes on the server* (Cause name, Finishing hit/damage, Amount, Highest amount by who), then the same per player. |
| **Highest hits** | Each player's biggest single hit: damage, weapon and enemy. |

```text
Weekly report — Mobs killed
**My World** · 25 Sep – 2 Oct 2026

Top 5 most mobs killed on the server
Name of mob       Amount  Highest hit  By who
                           received
---------------------------------------------
Greydwarf            350          300  Bjorn
Greydwarf Shaman      45          212  Astrid
Neck                  31           80  Astrid
```

**Layout.** Everything is tracked in full; every ranking shows its top 5. Tables are monospace and **never cut a name with "..."**: a name that does not fit wraps onto a second line of the same row, and a column is as wide as its longest text needs (up to a limit). A tab that does not fit in one Discord message (25 fields / 6000 characters per embed) continues in the next, marked "Page 2/3" in the footer. Per-player tables are shown for up to 40 players per tab, the Players table for up to 150; the rest is summarised as "and N more". A report is at most 24 messages. The test report is titled "Weekly report (test)".

**What counts and how.**

- Kills and damage are measured by the game that owns the creature, so that player needs the mod. Damage is the health a hit actually removed (after resistances); a killing blow counts for its full damage, not only the health the creature had left. Kills by tamed animals, other creatures or the environment are not credited. Bosses are not in the mob tables (see the Bosses tab).
- Deaths are reported by the dying player's own game. A death caused by another player is counted as a death with the cause "Killed by a player" and is not added to the mobs that killed players. Players are identified by character name.
- **English names everywhere.** Mobs, killers, death causes, enemies and weapons are always English, whatever language a player's game uses: the game's own English text when available, then a built-in table of the vanilla creatures (`Greydwarf_Elite` shows as "Greydwarf Brute"), then words built from the item key or prefab (`$item_sword_iron` becomes "Iron sword"). Totals saved by an earlier build with prefab names are converted when the server starts.
- **Environmental death causes** are read from the type of the killing hit: Drowning, Burning, Freezing, Poison, Water damage, Smoke, Edge of the world, Cinder fire, Ashlands ocean, Lava, Incinerator, Fall damage, Self-inflicted damage, Falling tree, Cart, Boat, Collapsing structure, Falling stalactite, Drawbridge, Turret, Catapult or Impact damage. A death with no hit shows as "Unknown / environment". The finishing hit shows its total damage and, when mostly one kind, its type (e.g. `88 Blunt`).
- **Highest hits** are credited to the player who landed them, also when another player's game owns the creature; the weapon is read from what the attacker holds.
- **Per-boss numbers.** For each boss kill the server also keeps how many times *that boss* killed a player (the same player dying three times counts three; deaths to another boss of the same kind do not count) and the biggest single hit it received, with who landed it and the weapon. A boss that wipes the party stays in the world, so these are collected per boss object and handed to the kill when it finally dies, even after the weekly cut-off. A boss fought but never killed is forgotten after 14 days. Bosses killed per player counts the killing blows.
- **Limits:** up to 500 players and 500 boss kills are tracked per period.

**Timing and delivery.**

- The report is posted the first time the clock passes `Server - Report Day` / `Report Time` (server time zone, DST-safe). If the server was off at that moment, it is posted when it runs again.
- **The statistics reset only after Discord has accepted every message.** A report Discord refused or that could not be delivered keeps the week's numbers and is tried again after 15 minutes (4 attempts in all; then the period is closed with an error in the log). A retry sends only the messages Discord has not accepted yet, so nothing is posted twice. The moment Discord accepts the report it is written to the data file, so a server that stops right afterwards does not post the same report again after the restart.
- Without a webhook URL the period simply ends when the report is due.
- Report messages bypass `Server - Queue Limit`, so a small limit or a busy chat can never drop part of a report.
- Running totals are saved (every ~30 s when something changed, and at shutdown) in `BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt`, together with the last 128 counted boss kills (those from the last 24 hours are read back), so a restart does not lose the week and a boss kill reported again is not counted twice. Delete the file to start over.
- Turning the report off and on again starts a fresh period.

### In-game weekly report window

Press `Home` (default) to open a window with the same numbers as the Discord report, as they stand right now. Press the key again, click **Close** or press `Escape` to close it. It is read-only; it never changes or resets the statistics.

- **Summary tiles:** players joined, mob kills, deaths, bosses defeated, and the highest damage dealt (with the player).
- **Tabs:** Players, Bosses, Mobs killed, Killer mobs, Death causes and Highest hits, with the same tables and columns as the Discord report. The **Players** tab is sortable (click a column header, click again to reverse); online players are green and you are gold. Long names wrap instead of being cut, long lists scroll.
- **Next report:** the day and time the next Discord report is due, converted to your own time zone, with a countdown.
- **Refresh:** the numbers are asked from the server when the window opens, every 30 seconds while it is open, and when you click **Refresh**. The server answers each player at most once per second.
- **What is sent:** the reply carries the top 100 players, the last 50 boss kills and the top 5 of each ranking per player; the totals are always complete. Players' games accept replies and period resets only from the server.
- **Behaviour:** while the window is open your character does not move or attack and the mouse cursor is free. The window can be dragged by its title bar and will not open while the chat has focus or the inventory, a menu, a shop or the map is open. Keyboard and mouse only; there is no gamepad control.
- **Size:** it opens compact. Drag the bottom-right corner to resize; the size is remembered (`Client - Window Width` / `Height`). It never becomes smaller than its content needs.
- It needs the mod on the server (this version or newer). Without it the window says the server did not answer. It is turned off for everyone with `[Weekly Report] Server - Window Enabled`, or while `[Weekly Report] Enable` is off; a notice at the top left says so.

#### Styling the window (CSS)

The window comes in two looks, chosen with `[Weekly Report] Client - Window Style`: **Classic** (the original flat look) and **Modern** (rounded cards and pill-shaped tabs, a gold Refresh button, a round icon and a diamond divider in the title, key hints in the footer, and Valheim's own rune font for the title and buttons). Both show the same numbers with the same layout; the choice applies the next time the window opens.

The window is drawn with Unity's UI Toolkit and styled by a USS-style sheet (a subset of CSS). The first time the window opens, the mod writes the style sheet of the chosen look to `BepInEx/config/` with every rule and colour: `ValheimDiscordRelay.report.uss` for Classic, `ValheimDiscordRelay.report.modern.uss` for Modern (each style has its own file). Edit it, close the window and open it again: the file is read each time the window opens, so you do not have to restart the game. Delete the file to get the original back.

```css
:root { --vdr-gold: #7FD1FF; }                 /* change a colour everywhere it is used */

.vdr-tile-value { font-size: 26px; }           /* bigger numbers in the tiles */
.vdr-tab:hover { background-color: #335577; }  /* hover look of a tab */
.vdr-window { border-color: #7FD1FF; }
```

- **Supported:** selectors by class (`.vdr-cell`), name (`#vdr-report-window`) and type (`Label`, `Button`), combined (`Label.vdr-cell`), with descendant (`a b`) and child (`a > b`) combinators and comma lists; the pseudo-classes `:hover`, `:active`, `:disabled`, `:enabled`; variables (`--name` in `:root`, used as `var(--name)`); colours as `#rgb`, `#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()`; lengths in `px` or `%`; and most USS properties (`color`, `background-color`, `font-size`, `-unity-font-style`, `-unity-text-align`, `opacity`, `margin`, `padding`, `border-width`, `border-color`, `border-radius` and more).
- **Not supported:** `@import` and other `@` rules, `url()`, transitions and animations, the `flex` and `border` shorthands. Anything the mod does not understand is skipped and listed as a warning in the BepInEx log with its line number; the rest of the file still applies.
- **Fonts:** `-unity-font-definition` takes one of Valheim's own fonts, `heading` (Norse), `heading-bold`, `body` (Averia Serif) or `body-bold`, for example `.vdr-title { -unity-font-definition: heading; }`. No other font can be used. Player names can contain characters these fonts do not have, so the Modern style uses Norse only for the title and the footer buttons. A font the game does not have is skipped (a line in the log says so).
- **Not styleable:** the layout: the heights of rows, tiles and tabs and the window's minimum size are fixed, so a much larger `font-size` is cut off. Changing `display`, `position` or the flex properties of the window's own containers can break the layout.
- **Classes:** `vdr-window`, `vdr-title`, `vdr-tile`, `vdr-tile-value`, `vdr-tile-caption`, `vdr-tab` (the selected one also has `vdr-tab-active`), `vdr-table-header`, `vdr-sort-btn`, `vdr-head-cell`, `vdr-row-alt`, `vdr-row-you`, `vdr-cell` (with `vdr-right`, `vdr-wrap`, `vdr-you`, `vdr-online`, `vdr-death`), `vdr-heading`, `vdr-rank-line`, `vdr-btn`, `vdr-btn-footer`, `vdr-btn-primary` (Refresh), `vdr-btn-close`, `vdr-title-bar`, `vdr-divider`, `vdr-footer-line`, `vdr-status`, `vdr-hint`, `vdr-warn`, `vdr-scroll-track` and `vdr-scroll-thumb`. The Modern style adds `vdr-badge` and `vdr-badge-bar` (the icon in the title), `vdr-diamond` (on the line under the title), and `vdr-key` and `vdr-key-text` (the footer key hints). The generated file lists the rules that use them.

### Version check

A server (a dedicated server, or a world hosted from the game's menu) lets a player in only when the player runs **exactly the same version of this mod as the server**. Refused: players without the mod, with an older version and with a newer version. They are disconnected before they enter the world and see Valheim's "Incompatible version" message (a game cannot show anything more specific to a player who does not have the mod). The server's log (`BepInEx/LogOutput.log`) says who was refused and why, for example `Disconnect: The client (Steam_7656...) has version 1.3.4; this server requires Valheim Discord Relay version 1.3.5 exactly.`

- Turn it off with `[General] Server - Require Same Version = false` (server only, not synced). It applies to the next player who connects.
- It only restricts who joins *your* server. A player who has this mod can still join other servers that do not have it; a game never becomes strict about the server it connects to.
- Builds from before this check do not announce their version, so they are refused too.
- Besides this check the mod declares `[NetworkCompatibility(VersionCheckOnly, Patch)]` for Jotunn: it adds no items or world data, so only the version is compared.
- The version compared is the mod's own version (`manifest.json` / the DLL). Update the server and the players together.
- With the check off, only players whose version matches take part in relaying chat, deaths, kills and so on.

### Delivery, queueing and retries

- Everything the **server** posts goes through one background queue with a pause of `Server - Minimum Send Interval Ms` between requests, so it never blocks the game and stays under Discord's rate limits.
- Each message is tried up to 5 times with growing delays. HTTP 429 waits for Discord's `Retry-After`; HTTP 5xx and network errors (no answer, DNS failure, refused or dropped connection) are retried; other rejections are given up on and logged.
- If more than `Server - Queue Limit` messages pile up, the oldest ordinary messages are dropped (the weekly report's are exempt). One chatty player cannot cause this on their own (see the chat spam protection).
- Uploads from players' games (screenshots, clips, videos) are sent directly by those games; see [Death videos](#death-videos-and-the-death-camera) for their retry rules.
- Every payload sets `allowed_mentions` to none, so nothing the mod posts can ping a user, role or `@everyone`.

## Files the mod creates

| File | Purpose |
| --- | --- |
| `BepInEx/config/com.kydldev.ValheimDiscordRelay.cfg` | All settings. |
| `BepInEx/config/ValheimDiscordRelay.report.uss` | Style sheet of the in-game report window, Classic style (created on first use). |
| `BepInEx/config/ValheimDiscordRelay.report.modern.uss` | The same for the Modern style (created the first time that style is used). |
| `BepInEx/cache/ValheimDiscordRelay.log` | The mod's own log, kept for 24 hours. |
| `BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt` | Weekly statistics and the counted boss kills (server). |
| `BepInEx/cache/ValheimDiscordRelay.BossAnnounced.txt` | Boss deaths already announced (server). |
| `BepInEx/cache/ValheimDiscordRelay.clean-shutdown` | Marks a clean shutdown so the next start can be told apart as a restart (server). |
| `BepInEx/cache/ValheimDiscordRelay.ScreenshotStrategy.txt` | The screenshot capture strategy that works on this PC (client). |
| `BepInEx/cache/ValheimDiscordRelay/WebPTool/` | The video encoder, unpacked from the DLL (client). |
| `<system temp>/ValheimDiscordRelay/` | Frames staged for a video while it is encoded; removed afterwards. |

## Security and privacy

- **Players can read the shared webhooks.** The webhooks players' games upload to (screenshots, clips, death videos, boss videos) are sent to every client, so any player can read them (for example in Configuration Manager) and post to that channel. Use a dedicated webhook and channel for each, never one that can reach a staff channel, and delete the webhook in Discord if it leaks. The server logs one line at start-up naming which of them are set.
- **Webhooks only the server uses** (chat, server notifications, player notifications, weekly report) are never synced.
- **Lock the configuration.** Keep `[General] Server - Lock Configuration` on (the default); otherwise any connected player can change synced settings, webhooks included.
- **What leaves a player's PC:** only what the features above send, to the Discord webhooks you configured: chat text, the screenshots / clips / videos the player triggers (or that a death or boss kill triggers), and death messages. A screenshot is the whole rendered screen. The mod makes no other network requests; Discord itself fetches the boss portraits from the third-party hosts named above.
- **Frame buffer.** While death videos are on, or clips are ready, the last ~4 seconds of the game screen are held in memory (never written to disk unless a video is being made from them).
- **Opt-out per player.** Every `Client - Enabled` switch lets a player keep their chat, deaths, kills, screenshots and clips out of Discord and the report.
- **Hardening:** webhook payloads are JSON-escaped, mentions are disabled, names are sanitised, server RPC handlers bound every count and size and clamp NaN / infinite / negative numbers, unknown players and protocol versions are rejected, and HTTPS requests enable TLS 1.2 without disabling protocols other mods use.

## Performance

- Screen capture is GPU-side with asynchronous readback; frames are scaled down on the GPU before they are read back, and disk writes, encoding and uploads run on background threads.
- The rolling buffer only runs when death videos are on or clips are ready; otherwise nothing is captured.
- Video encoding uses at most a quarter of the logical CPU cores at below-normal priority.
- The screenshot render texture is freed a minute after the last screenshot.
- The server's work is a few small RPCs and one background webhook thread; the in-game window redraws only when something changes.

## Logs and troubleshooting

Everything is written to `BepInEx/cache/ValheimDiscordRelay.log` (kept for 24 hours), and the important lines also appear in the normal BepInEx log. Common causes when nothing arrives in Discord:

- The webhook URL for that feature is empty. For screenshots, clips and videos it must be set in the **server's** config.
- The feature's `Enable` is off, or `Client - Enabled` is off on that client.
- A player gets "Incompatible version" when joining: their copy of this mod is missing or is not the server's version (see [Version check](#version-check); the server log names the version it found). Install the server's version, or set `[General] Server - Require Same Version` to `false`.
- Settings seem missing or wrong after an update: delete the old config file (see [Installation and updating](#installation-and-updating)).
- **Boss video:** `Client - Video Enabled` is off on the recording client. With `Last Attacker`, that player's `[Player Deaths] Client - Video Enabled` must also be on.
- **Clips:** nothing happens on the key press: check that `[Clips] Server - Webhook URL` is set on the **server**, that both `Enabled` switches are on, and that the key is not `None`. The log says "Clip hotkey pressed" when the key is recognised.
- **No video, message only:** the video encoder could not run (it is a Windows program) or every encode failed; the log has the reason.
- **Mob kills or damage missing from the weekly report:** the player who owned the creature does not have the mod, or has `[Weekly Report] Client - Enabled` off.
- **Weekly report not posted:** check `[Weekly Report] Server - Webhook URL` and the due day / time (server time zone). After a failed delivery the log says when it will be retried.
- **Report window does nothing on the key press:** check `Client - Window Key` is not `None`, and that you are not in the chat, the inventory or a menu. A notice at the top left says when the admin has turned the report or the window off.
- **Report window looks wrong after editing the `.uss` file:** the log lists every line that was not understood (`Style sheet ValheimDiscordRelay.report.uss, line N: ...`; `...report.modern.uss` for the Modern style). Delete the file to get the default look back.
- **Modern style: the title or buttons show boxes or nothing:** Valheim's rune font could not be used. Look for `[Report] ... font` lines in the log. Remove the two `-unity-font-definition: heading;` lines from `ValheimDiscordRelay.report.modern.uss` (or delete the file and set `Client - Window Style` to `Classic`).
- **Report window does not appear, or has no text:** the window uses Unity's UI Toolkit. The log has a line `[Report] The weekly report window uses UI Toolkit (font: ...)` when it was created, or an error line `Could not create the weekly report window` with the reason. Please include these lines when reporting a problem.
- **Report window says the server did not answer:** the mod (this version or newer) is not installed on the server, or you are not connected to one.
- **Black screenshots:** see [Manual screenshots](#manual-screenshots); the log line "Screenshot context: ..." says whether the screen was really black.

Every screenshot, death, boss and report event writes a line to the log (for example `[Report] Death reported ...`), which is the first place to look. Set `[General] Server - Log Successful Sends = true` to log every delivery too.

## Building from source

- Layout: `src/Bootstrap`, `Core`, `Configuration`, `Features/<Feature>`, `Shared`, `Presentation`, `Integrations/Harmony`, `Testing`; tests in `tests/` (unit, integration, TestLab suites). Details: `docs/Architecture.md`.
- `ValheimDiscordRelay.csproj` (.NET Framework 4.8) takes the shared settings from `Claude\shared\Kydl.Mod.props`; game and BepInEx assemblies come from the shared reference folder. The DLL ends up in `assets/`.
- The embedded video encoder in `WebP/EmbeddedWebP/` is downloaded by the build itself when it is missing (`WebP/prepare-webp.ps1`, official libwebp, SHA-256 checked; this is why the first build needs internet access; `-p:KydlSkipWebP=true` skips it and builds without clip support).
- The quote lists live in `src/Features/PlayerDeaths/Infrastructure/DeathQuotes.cs` and `src/Features/BossDeath/Infrastructure/BossQuotes.cs`; the boss list in `src/Features/BossDeath/Domain/BossCatalog.cs`.
- Tests: `dotnet test tests/Unit`, `dotnet test tests/Integration`; the in-game suites run with DrakesTestLab (`tests/TestLab/suites`).
- See `CHANGELOG.md` for what changed in each version.

## Credits and licence

I made this mod because I wanted something I could not find in the other available mods. It might not be perfect, but it has worked in my tests and I hope it works for you too. Please report any issues.

The mod bundles a copy of [ServerSync](https://github.com/blaxxun-boop/ServerSync) (Copyright 2021 Tykea; permission notice in `Common/ServerSync/LICENSE.txt`), with local changes for the strict version check described in that file's header, and the official libwebp `img2webp` encoder from Google's WebP project.
