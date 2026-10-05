# Valheim Discord Relay

One BepInEx plugin DLL for Valheim that connects your world to Discord through webhooks:

- **Chat relay** — normal chat and shouts, with their own webhooks.
- **Death messages and death videos** — a funny, randomly chosen death message plus a short WebP clip of the player's death.
- **Boss announcements** — an embed with the top damage dealer, plus a short video of the boss dying.
- **Server notifications** — server up / down / restart.
- **Player notifications** — "has joined the world" / "had to leave us", with the current population.
- **Weekly report** — one embed per week: players joined, mob kills and deaths per player, bosses killed, highest damage dealt.
- **Manual screenshots** — one hotkey, straight from the client.

I've done this mod because I wanted something that I could not find in other of the available mods. This mod might not be perfect, but it has worked for me in my tests and I hope it does work for those that do install it. Please report any issues.

## NOTE!!!

Remove the old config file (`BepInEx/config/ValheimDiscordRelay.cfg`) before starting the game after an update. Settings were added, renamed and removed (for example `[Server Notifications] Player Arrival` is gone — see [Player notifications](#player-notifications)).

## Requirements

- Valheim 1.0.12
- BepInEx 5.4.2350
- Install the mod on the dedicated server and on every client that should relay chat, report deaths / kills, or use client capture features.
- Worlds hosted from Valheim's own menu (no dedicated server) are supported: install the mod on the host and set all the webhook URLs in the host's config (the host is the server). The host's own chat is relayed directly; other players' chat arrives as usual, so they need the mod too.

## Installation

Install the Thunderstore/Hexium package, or place `ValheimDiscordRelay.dll` in:

`BepInEx/plugins/ValheimDiscordRelay/`

The package contains one mod DLL. It contains both client and server components; the server part stays inactive on clients.

## What goes where

Every feature that sends something to Discord has its own category, its own webhook setting and its own enable switch, so you can run only the parts you want and point each at its own channel.

| Feature | Category | Sent by | Webhook setting | Sent as |
| --- | --- | --- | --- | --- |
| Normal chat | `[Chat]` | server | `Server - Normal Webhook URL` (falls back to `Server - Webhook URL`) | the player's name |
| Shouts | `[Chat]` | server | `Server - Shout Webhook URL` (falls back to `Server - Webhook URL`) | the player's name |
| Manual screenshots | `[Screenshots]` | client | `Server - Webhook URL` | the player's name (optional) |
| Death message + video | `[Player Deaths]` | client | `Server - Webhook URL` | the player's name |
| Boss announcement | `[Boss Death]` | server (embed) + client (video) | `Server - Webhook URL` | `Server - Webhook Username` |
| Server up / down / restart | `[Server Notifications]` | server | `Server - Webhook URL` | `Server notifications` (or the webhook's own name) |
| Player joined / left | `[Player Notifications]` | server | `Server - Webhook URL` | `Player notifications` |
| Weekly report | `[Weekly Report]` | server | `Server - Webhook URL` | `Weekly report` |

## Configuration

The mod uses one shared file:

`BepInEx/config/ValheimDiscordRelay.cfg`

### How the settings are named

Settings are grouped by feature, one category per feature, and every setting name starts with who it belongs to:

- **`Server - ...`** — set by the server admin. Either synced from the server to every player, or used only on the server (see the table below).
- **`Client - ...`** — each player's own setting, kept in that player's own config. Nothing about it is synced.

Every category has an **Enabled** switch so a feature can be turned off without touching the rest:

- `Server - Enabled` — the admin's switch. Turning it off disables the feature for everyone. (Where the switch is synced, players receive the server's value.)
- `Client - Enabled` — the player's switch. Turning it off opts that player's game out. Only categories where the player's game does something have one.

A feature with both switches runs only when **both** are on. `[Admin]` has no Enabled switch because it is not a feature; it only controls the others.

BepInEx writes a description above every setting in that file; the example below is shortened. Categories are written in this order.

```ini
## ---- Admin: config lock and delivery logging ----------------------------
[Admin]
Server - Lock Configuration = true
Server - Log Successful Sends = false

## ---- Chat: chat relay ----------------------------------------------------
[Chat]
Server - Enabled = true
Server - Webhook URL =
Server - Normal Webhook URL =
Server - Shout Webhook URL =
Server - Max Message Length = 1800
Server - Name Display = NameOnly
Server - Shout Prefix = <ESC>[2;31m
Server - Normal Prefix = <ESC>[2;36m
Server - Queue Limit = 100
Server - Minimum Send Interval Ms = 250
Client - Enabled = true
Client - Max Message Length = 1000

## ---- Manual screenshots --------------------------------------------------
[Screenshots]
Server - Enabled = true
Server - Webhook URL =
Client - Enabled = true
Client - Key = PageUp
Client - Include Player Name = true

## ---- Player deaths: message and video ------------------------------------
[Player Deaths]
Server - Enabled = true
Server - Webhook URL =
Client - Enabled = true
Client - Video Enabled = true
Client - Video FPS = 30
Client - Video Resolution = 960x540
Client - Video Pre Duration = 4
Client - Video Post Duration = 4

## ---- Boss kills ----------------------------------------------------------
[Boss Death]
Server - Enabled = true
Server - Webhook URL =
Server - Webhook Username = Boss Death Announcement!
Server - Video Source = Boss Owner
Client - Enabled = true
Client - Video Enabled = true
Client - Video FPS = 30
Client - Video Resolution = 960x540
Client - Video Pre Duration = 4
Client - Video Post Duration = 4

## ---- Server up / down / restart -------------------------------------------
[Server Notifications]
Server - Enabled = true
Server - Webhook URL =
Server - Use Discord Webhook Name = false
Server - Up Notification = true
Server - Down Notification = true
Server - Restart Notification = true
Server - Restart Window Seconds = 120

## ---- Player joined / left -------------------------------------------------
[Player Notifications]
Server - Enabled = true
Server - Webhook URL =
Server - Arrival Avatar URL =
Server - Leave Avatar URL =

## ---- Weekly report --------------------------------------------------------
[Weekly Report]
Server - Enabled = true
Server - Webhook URL =
Server - Report Day = Sunday
Server - Report Time = 20:00
Server - Send Test Report = false
Client - Enabled = true
```

`<ESC>` stands for the real escape character (ASCII 27) that BepInEx writes into the file for the two ANSI prefixes; leave those two settings alone unless you want other colours.

### What is synced and who can change it

The mod uses ServerSync. The server's values for the settings in the middle column are sent to every connected player, and a change made in-game (Configuration Manager, F1) by an admin flows back to the server, which saves it. **Webhooks are set by the admin, on the server.** Players do not enter their own: a client's own value for a synced setting is replaced by the server's while connected.

| Category | Synced (admin-controlled) | Server only, never synced | Per player (`Client - ...`) |
| --- | --- | --- | --- |
| `[Admin]` | `Server - Lock Configuration`, `Server - Log Successful Sends` | — | — |
| `[Chat]` | `Server - Enabled`, `Server - Max Message Length`, `Server - Name Display`, `Server - Shout Prefix`, `Server - Normal Prefix`, `Server - Queue Limit`, `Server - Minimum Send Interval Ms` | the three chat webhook URLs | `Client - Enabled`, `Client - Max Message Length` |
| `[Screenshots]` | `Server - Enabled`, **`Server - Webhook URL`** | — | `Client - Enabled`, `Client - Key`, `Client - Include Player Name` |
| `[Player Deaths]` | `Server - Enabled`, **`Server - Webhook URL`** | — | `Client - Enabled`, `Client - Video ...` |
| `[Boss Death]` | `Server - Enabled`, `Server - Webhook Username`, `Server - Video Source`, **`Server - Webhook URL`** | — | `Client - Enabled`, `Client - Video ...` |
| `[Server Notifications]` | `Server - Use Discord Webhook Name` | `Server - Enabled`, `Server - Webhook URL`, the up / down / restart switches, `Server - Restart Window Seconds` | — |
| `[Player Notifications]` | `Server - Enabled`, `Server - Arrival Avatar URL`, `Server - Leave Avatar URL` | `Server - Webhook URL` | — |
| `[Weekly Report]` | `Server - Enabled`, **`Server - Report Day`, `Server - Report Time`, `Server - Send Test Report`** | `Server - Webhook URL` | `Client - Enabled` |

In short:

- **Webhooks only the server uses** (chat, server notifications, player notifications, weekly report) live in the server's config only. They are never sent to clients, so no client can see or change them.
- **Webhooks the clients upload to** (screenshots, death videos, boss videos) are set by the admin in the *server's* config and synced to every client. Players cannot set their own.
- **The weekly report schedule and the test trigger** are synced and admin-controlled.

### `[Admin]`

- `Server - Lock Configuration` (default `true`): only players listed in the server's `adminlist.txt` can change the synced settings, which includes all of the above. Set it to `false` to let ANY connected player change them — webhook URLs and the report schedule included — so leave it on for a public server.
- `Server - Log Successful Sends` (default `false`) — log every successful Discord delivery from every category: chat, server notifications, player notifications, boss deaths and the weekly report (in the server's log), and screenshot / death video / boss video uploads (in the log of the game that uploaded them).

### `[Chat]`

- `Server - Enabled` — enable the server-side chat relay.
- `Server - Webhook URL` — fallback Discord webhook, used when the specific Normal/Shout webhook is empty.
- `Server - Normal Webhook URL` — webhook for normal chat; falls back to `Server - Webhook URL`.
- `Server - Shout Webhook URL` — webhook for shouts; falls back to `Server - Webhook URL`.
- `Server - Max Message Length` — maximum chat text sent to Discord (100–1900).
- `Server - Name Display` — the Discord username format: `NameOnly` (`Bjorn`), `NameWithNumber` (`Bjorn [1]`, `Bjorn [2]` for duplicate character names) or `NameWithIdSuffix` (`Bjorn [5678]`, the last 4 digits of the platform ID). Default `NameOnly`.
- `Server - Shout Prefix` — ANSI prefix for shouts; default is dim red.
- `Server - Normal Prefix` — ANSI prefix for normal chat; default is dim cyan.
- `Server - Queue Limit` — maximum queued Discord messages; the oldest are dropped when it is full (10–1000).
- `Server - Minimum Send Interval Ms` — pacing between webhook requests (50–5000).
- `Client - Enabled` — send the chat typed in this game to the server for relaying. Turn off to keep your own chat out of Discord.
- `Client - Max Message Length` — maximum chat text sent from the client to the server (1–1000).

### `[Screenshots]`

- `Server - Enabled` — synced. Turn off to disable manual screenshots for every player.
- `Server - Webhook URL` — Discord webhook for manual screenshots. Set by the admin in the server's config and synced to every client.
- `Client - Enabled` — allow the screenshot hotkey in this game.
- `Client - Key` — screenshot hotkey; default `PageUp`. Set to `None` to disable.
- `Client - Include Player Name` — use the local Valheim character name as the Discord username for manual screenshots.

### `[Player Deaths]`

- `Server - Enabled` — synced. Turn off to disable death messages (shout, Discord message and video) for every player.
- `Server - Webhook URL` — Discord webhook for death videos and the death message. Set by the admin in the server's config and synced to every client.
- `Client - Enabled` — announce this player's deaths: the in-game shout and the Discord message. Turn off to keep your deaths out of chat and Discord.
- `Client - Video Enabled` — record a death video. When off, the death message is still sent, without a video. See [Death messages](#death-messages).
- `Client - Video FPS` — `20` or `30`.
- `Client - Video Resolution` — `480x270`, `640x360` or `960x540`.
- `Client - Video Pre Duration` / `Client - Video Post Duration` — `2` to `4` seconds before / after death.

The default death-video configuration is **30 FPS, 960x540, 4 seconds before death and 4 seconds after death**.

### `[Boss Death]`

- `Server - Enabled` — announce boss deaths. Synced.
- `Server - Webhook URL` — the boss webhook. The server posts the embed to it, and the player whose game owns the boss uploads the video to it. Set it once on the server; it is synced to every client.
- `Server - Webhook Username` — name shown as the sender of both messages; default `Boss Death Announcement!`.
- `Server - Video Source` — whose game records the boss video: `Boss Owner` (default) or `Last Attacker`. Synced from the server and admin-controlled. **With `Last Attacker` the video is recorded by that player's own game, so that player's `[Player Deaths] Client - Video FPS` and `Client - Video Resolution` apply (and `[Player Deaths] Client - Video Enabled` must be on), not the boss owner's and not the admin's.** See [Boss death announcements](#boss-death-announcements).
- `Client - Enabled` — take part in boss announcements from this game: measure the damage you deal to bosses and record the boss video when asked. Turn off to opt out completely.
- `Client - Video Enabled` — upload the boss death video (on the game that records it).
- `Client - Video FPS` (`20` or `30`), `Client - Video Resolution` (`480x270`, `640x360`, `960x540`) — only used when `[Player Deaths] Client - Video Enabled` is off (see [Boss death announcements](#boss-death-announcements)); never used for a `Last Attacker` video.
- `Client - Video Pre Duration`, `Client - Video Post Duration` — `2` to `4` seconds before / after the boss dies (the recording player's own values).

### `[Server Notifications]`

- `Server - Enabled` — enable server up/down/restart notifications.
- `Server - Webhook URL` — the webhook for them.
- `Server - Use Discord Webhook Name` — unchecked: messages are sent as **Server notifications**. Checked: no username is sent, so Discord shows the name set on the webhook itself.
- `Server - Up Notification`, `Server - Down Notification`, `Server - Restart Notification` — turn each message on or off.
- `Server - Restart Window Seconds` — a startup within this many seconds of a clean shutdown (including boot and world load) is reported as a restart; a later one as "is up" (10–3600, default 120).

### `[Player Notifications]`

- `Server - Enabled`, `Server - Webhook URL`, `Server - Arrival Avatar URL`, `Server - Leave Avatar URL` — see [Player notifications](#player-notifications).

### `[Weekly Report]`

- `Server - Enabled`, `Server - Webhook URL`, `Server - Report Day`, `Server - Report Time`, `Server - Send Test Report` — see [Weekly report](#weekly-report).
- `Client - Enabled` — include this player in the report: this game measures the kills and damage you deal and reports your deaths to the server. Turn off to opt out.

## Chat relay

The client sends only the chat type and text; the server resolves the sender (character name, platform identifier) from the actual network peer. No Discord account linking is used.

Normal chat uses dim cyan ANSI formatting (`ESC[2;36m`); shouts use dim red (`ESC[2;31m`). Whisper/private chat is intentionally not relayed.

The server delivers webhooks sequentially, with queueing, pacing, and retry handling for Discord rate limits and temporary HTTP errors.

Valheim's own "I have arrived!" shout is a special case: it is dropped while `[Player Notifications] Server - Enabled` is on, and relayed like any other shout when it is off. See [Player notifications](#player-notifications).

Death messages are shouted in game chat, but that shout is **not** sent to the normal Discord chat webhook; Discord gets the death message through the death webhook instead.

## Manual screenshots

Press `PageUp` by default to capture the final rendered Valheim frame, including UI from other mods. Screenshots are encoded as PNG and uploaded directly from the client — they do not pass through the Valheim server. The temporary local file is deleted after upload.

## Death messages

When the local player dies, the client works out what killed them from Valheim's own hit data, picks a random message for that kind of death, and:

1. **shouts it in game chat** 5 seconds after the death, so it stays out of the video (everybody in the world sees it; the shout is not relayed to the chat webhook), and
2. **sends it to Discord** through `[Player Deaths] Server - Webhook URL`, together with the death video (or alone, see below).

Both the shout and the Discord part need `[Player Deaths] Server - Enabled` and `Client - Enabled` on. The Discord part also needs a `Server - Webhook URL`; if it is missing the shout still happens and the log says why nothing was sent to Discord. With `Client - Video Enabled` off the Discord message is still sent, just without a video. Deaths are counted for the [weekly report](#weekly-report) whatever the `[Player Deaths]` settings are (the report has its own switches).

### How the killer is found

- The attacker (name, level, boss flag, prefab) is snapshotted when it hits you, so it is still named correctly if it dies or despawns before you do.
- Burning and poison deaths whose final tick has no attacker are credited to the creature that hit you within the previous 5 seconds.
- Creature level is shown as stars: none for level 1, one ⭐ for level 2, two ⭐⭐ for level 3, and so on up to five. In Discord they are emoji; in in-game chat they are written out — `(1 star)`, `(2 stars)` — because Valheim's font cannot draw emoji.
- Animals are recognised by prefab name: Boar, Neck, Bjorn (the bear), Serpent, Leech, Wolf, Bat, Bat_swamp, Lox, Deathsquito, Volture, Asksvin, Moose. 

### Quote lists

The messages are picked at random (never the same one twice in a row within a list) from preset lists.

| Killed by | List | Entries |
| --- | --- | --- |
| an animal with no stars | `AnimalNoStar` | 10 |
| an animal with one star | `AnimalOneStar` | 10 |
| an animal with two or more stars | `AnimalMultiStar` | 10 |
| any other non-boss enemy, or another player | `NonAnimal` | 11 (the last is the original message) |
| the environment | `Environmental` | 11 (the last is the original message) |

**Placeholders**

| Placeholder | Replaced with |
| --- | --- |
| `{Player.name}` | the player's name |
| `{Creature.name}` | the killer's name. Animals are written in lower case mid-sentence and capitalised at the start of a sentence; every other enemy keeps the name Valheim shows (`Greydwarf Brute`, `Troll`) |
| `{level}` | the killer's stars, with a leading space: ` ⭐⭐` in Discord, ` (2 stars)` in game chat; empty for a creature with no stars |
| `{star}` | one star: ⭐ in Discord, the word "star" in game chat |
| `{Cause}` | environmental quotes only: what killed the player (table below) |

Example quote (from `AnimalMultiStar`):

```text
{Player.name} was slain by a {Creature.name}{level}. In fairness, that thing was basically a natural disaster with legs.
```

For a player called Astrid killed by a 2-star Boar it becomes:

```text
Game chat:  Astrid was slain by a boar (2 stars). In fairness, that thing was basically a natural disaster with legs.
Discord:    Astrid was slain by a boar ⭐⭐. In fairness, that thing was basically a natural disaster with legs.
```

The last quote in `NonAnimal` is the original message:

```text
{Player.name} got killed by a {Creature.name}{level}. The {Creature.name} is dancing on {Player.name}'s tombstone.
```

### Boss and special-enemy deaths

Used for real bosses and for special enemies whose display name carries a `<color=...>` tag (for example Geirrhafa). This message is fixed, not random:

```text
{Boss} managed to get one over {Player}. {Boss} roams triumphant, for now...You got this!
```

In Discord a colour-tagged name is shown in red (ANSI); in-game chat shows the plain name only, and the stars follow the name as described above.

### Environmental deaths

Deaths caused by drowning, burning, freezing, poison, water, smoke, the edge of the world, Cinder Fire, Ashlands ocean, Ashlands lava, the incinerator, falling or self-damage pick one of the 11 `Environmental` quotes at random. One quote names the cause through `{Cause}`:

```text
{Player.name} has discovered one of Valheim's many creative ways to die, {Cause}.
```

| Hit type | `{Cause}` |
| --- | --- |
| Drowning | drowning |
| Burning | burning |
| Freezing | freezing |
| Poisoned | poisoning |
| Water | a swim gone wrong |
| Smoke | smoke inhalation |
| Edge of the world | the edge of the world |
| Cinder Fire | cinder fire |
| Ashlands ocean | taking a dip in the Ashlands ocean |
| Ashlands lava | taking a dip in lava |
| Incinerator | getting incinerated |
| Fall | falling |
| Self | self-inflicted damage |

### Trees, carts, boats and other impact deaths

These use one fixed sentence each and are not random:

```text
{Player} got crushed by a falling tree.
{Player} got crushed by a cart.
{Player} got crushed by a boat.
{Player} got crushed by a collapsing structure.
{Player} got crushed by a stalagmite.
{Player} got crushed by a drawbridge.
{Player} got crushed by a turret.
{Player} got crushed by a catapult.
{Player} got crushed by an impact.
```

### Fallbacks

- If Valheim stored its own death text for the player, that is used (`{Player} <text>`).
- If the cause cannot be resolved from the available hit data: `{Player} got killed by Unknown.`
- **Repeat deaths:** if the player dies again while the previous death is still being recorded or encoded (or within 30 seconds of it), no second video is made; Discord gets a three-line message instead (in one code block): the normal random death quote, then `{Player} died again in a very short time, maybe fight an enemy of your size: T.W.I.G.`, then `No video recording of this unfortunate death will be presented.`

### Discord formatting

Every Discord death message is sent inside a code block. When the enemy name carries a colour tag, the block is an `ansi` block and the name is red; otherwise it is a plain block. The upload is sent as the player's name.

## Death videos

When the local player dies, the client records the configured period immediately before and after the death and uploads the result as an animated WebP, directly from the client, to the configured `[Player Deaths] Server - Webhook URL`. Each upload carries the death message from [above](#death-messages).

The rolling pre/post-death frame buffer is kept in memory, not on disk. Disk is only touched once a death actually happens, to write the relevant frames out for `img2webp` (an external process, so it needs real files) — those staged frames and old WebP outputs are then swept automatically (a background check every 5 minutes removes anything older than 24 hours).

WebP quality/compression is chosen automatically and isn't user-configurable: failed attempts retry internally with more compact settings, and if every attempt fails, the client sends only the death message as text. Frames are staged under a short path in the system temp directory (`%TEMP%\ValheimDiscordRelay\DeathEncoding\...`) rather than the full Gale/BepInEx cache path, to keep the `img2webp` command line short. The encode itself runs in parallel: the frames are split into chunks that separate `img2webp` processes (libwebp) encode at the same time, then joined into one animated WebP. At most a quarter of the machine's logical processors (minimum one) are used for this, shared between death and boss clips, and the encoders run at below-normal priority.

## Boss death announcements

Use `[Boss Death]` for boss kill announcements. When a boss dies, the server posts an embed to the boss webhook, and — at the same time, as a separate message — the player whose game owned the boss (or, with `Server - Video Source = Last Attacker`, the player who landed the last hit) uploads a short video of the moment it died.

**The embed**

- Webhook avatar and embed thumbnail: the boss's portrait.
- Title: `{Boss} has fallen!`
- Description: depends on who damaged the boss, followed by the boss's picture below the text. Discord cannot align embed images, so the picture is served through the wsrv.nl image proxy, which places it in the middle of a transparent 4:3 canvas. This is one switch (`CenterPhoto`) in `Common/BossCatalog.cs` if you would rather use the original image URL.
  - Two or more players dealt damage: one of 11 **party** quotes, picked at random, naming the top damage dealer and their damage.
  - One player dealt damage: one of 11 **solo** quotes, picked at random, with the damage that player dealt.
  - No damage was recorded: `{Boss} has been defeated.`
- The same quote is never used twice in a row. All quotes are in **`Server/BossQuotes.cs`**; edit the lists there to add or change them.
- Boss and player names start with a capital letter.

Placeholders: `{Boss}` (the boss's name), `{Player}` (the player's name — the top damage dealer in a party quote), `{Damage}` (the damage, e.g. `12,345` — the player's own in a solo quote, the top dealer's in a party quote). `\n\n` in a quote is a blank line.

Examples:

```text
Party:  You are stronger together, Bonemass had no chance against such a strong team. Astrid managed to do 4,210 to Bonemass.
Solo:   Solo victory! Eikthyr never stood a chance against that much stubbornness.

        Good job, Astrid! You've somehow managed to deal 1,870 damage to it.
```

**How damage is counted.** Valheim does not keep per-player damage totals, so the mod measures the health each hit removes from the boss (after resistances), per attacking player. Valheim only applies damage on the game that owns the boss, so that client reports the numbers to the server in small batches; the server adds them up per boss, so the totals survive the boss changing owner mid-fight. The mod must be installed on the client that owns the boss for its damage to be counted.

**The video.** One game records and uploads it, chosen by `Server - Video Source` (below). By default (`Boss Owner`) that is the client that owns the boss when it dies (that is normally someone near it). Its pre and post durations are `Client - Video Pre Duration` / `Client - Video Post Duration`.

- While `[Player Deaths] Client - Video Enabled` is **on** (and death messages are enabled for that player), the boss video is cut from the frames the player-death recorder is already capturing, so it uses the **death video's FPS and resolution** and a boss fight costs no extra screen capture or encoding.
- While it is **off**, a separate recorder runs only while that client is fighting a boss and uses `[Boss Death] Client - Video FPS` and `Client - Video Resolution`.

When a video is going to be uploaded, the announcement embed is held back until the video has been encoded and is about to upload, so the two appear in Discord together (embed first, the video as soon as its upload finishes). If the video is not ready within 75 seconds, or you leave the game first, the embed is sent anyway. If the `[Boss Death] Server - Webhook URL` is not set, or `Client - Video Enabled` is off on the recording client, no video is uploaded and the embed is posted immediately by the server.

**`Server - Video Source`: Boss Owner or Last Attacker.** The boss owner is whoever's game happens to own the boss when it dies, and that player can be facing away from it, which gives a video of nothing. `Last Attacker` records the video on the game of the player who landed the last hit instead, who is almost always looking at the boss. It is off by default (`Boss Owner`) and is a synced, admin-controlled setting.

- **The recording player's settings apply, not the admin's and not the boss owner's:** `[Player Deaths] Client - Video FPS` and `Client - Video Resolution`, `[Boss Death] Client - Video Pre Duration` / `Client - Video Post Duration`, and `Client - Video Enabled`. The boss video is cut from the death-recorder frames, so `[Player Deaths] Client - Video Enabled` has to be **on** for that player; otherwise their game declines.
- How it works: the boss owner remembers who last damaged the boss (a player hit more than 20 seconds before the kill does not count), and when the boss dies it asks that player's game to record, through a message that the server simply forwards. The owner keeps recording its own clip as a fallback and drops it as soon as the attacker's game accepts.
- **Fallback to the boss owner's video:** if the last attacker has no mod or an older build, has video off, is already busy with another clip, or does not answer within about 1.5 seconds after the clip ends, the boss owner's own video is used. A second video is never posted: if the attacker answers too late, the owner tells it to stand down.
- If the boss owner itself landed the last hit, nothing changes (its own video is the last attacker's video). With a dedicated server that owns the boss, `Last Attacker` is the only way to get a video at all, because a dedicated server cannot record one.
- The announcement embed is still held back until the video is ready, now waiting for the last attacker's game to report it; the 75 second limit still applies. If the attacker's game accepts but then fails to encode or upload, the embed is posted without a video.
- Mixed versions are safe: the settings sync by name, and a game without this feature just never answers.

**Which bosses.** Known bosses are recognised by prefab name: `Eikthyr`, `gd_king`, `Bonemass`, `Dragon`, `GoblinKing`, `SeekerQueen`, `Fader`, `FrozenKing`. Kall Fimbulbringer fights in three phases, each its own object; only the death of the final phase is announced, and the damage from the earlier phases is added into that announcement. Any other boss is announced with its in-game name and no images, and the log says which prefab it was.

## Server notifications

`[Server Notifications]` posts when the server starts, stops or restarts:

```text
Server **My World** is up.
Server **My World** restarted.
Server **My World** is going down.
```

- The server name is **bold** in every message.
- Messages are sent with the username **Server notifications**. Tick `Server - Use Discord Webhook Name` to send no username at all, so Discord shows the name set on the webhook itself (Channel settings → Integrations → Webhooks).
- A clean shutdown followed by a startup within `Server - Restart Window Seconds` is reported as a restart; a longer gap as "is up". The "down" message is sent synchronously so it is not lost when the process exits.

## Player notifications

`[Player Notifications]` has its own webhook, and posts when someone joins or leaves, sent as **Player notifications**:

```text
{Player.name} has joined the world. Population: {players.count}. Good luck!
{Player.name} had to leave us. Hope they had fun. Population: {players.count}.
```

`Population` is the number of players online after the join / leave (the host of a game-hosted world counts too).

- `Server - Enabled` (synced, admin-controlled).
  - **On:** Valheim's own arrival message ("I have arrived!") is intercepted on the client and ignored — it is **not** sent to any Discord webhook. The mod posts the messages above instead.
  - **Off:** nothing is intercepted. The game's arrival message is relayed like any other shout through the normal chat webhook, and no join/leave messages are posted.
- `Server - Webhook URL` — the webhook for these messages. Server-side only; never sent to clients.
- `Server - Arrival Avatar URL` / `Server - Leave Avatar URL` — optional http/https image URLs used as the webhook icon for join / leave messages. Leave one empty to use the webhook's default Discord icon. Both are synced and admin-controlled.

**How joins and leaves are detected.** Valheim sends "I have arrived!" on *every* spawn (including after each death) and sends **no message at all when a player leaves** — the game has no such text. So the server watches the connected players instead: one join per connection, one leave per disconnect, nothing on respawn. A server shutdown is not announced as everybody leaving.

## Weekly report

`[Weekly Report]` posts one embed per week through its own webhook, sent as **Weekly report**. Its code lives in its own `Report/` folder.

- `Server - Enabled` — synced from the server. Clients only collect and send numbers while it is on.
- `Client - Enabled` — this player takes part in the report. Turn it off on a client and that player's kills, damage and deaths are not collected.
- `Server - Webhook URL` — the report webhook. Server-side only; never sent to clients.
- `Server - Report Day` — default `Sunday`. Synced; only admins can change it (while `[Admin] Server - Lock Configuration` is on).
- `Server - Report Time` — `HH:mm`, in the server's local time zone; default `20:00`. Synced; only admins can change it.
- `Server - Send Test Report` — debug helper. Set to `true` to post the report as it stands right now, marked "(test)". It does not reset the statistics and turns itself back to `false`. It fires when an admin changes the setting (Configuration Manager, or a live config reload on the server) or at the next server start. Synced; only admins can trigger it, and clients never act on it.

The embed covers the period since the previous report:

```text
Weekly report
**My World** · 25 Sep – 2 Oct 2026

Players joined      Bosses defeated        Highest damage dealt
3                   2                      Astrid
                    Eikthyr, Bonemass      48,213 damage

Kills & deaths
Player          Kills  Deaths
Astrid            212       3
Bjorn              96       1
Ragnhild           41       0
```

- **Players joined** — the number of different players (by character name) that joined.
- **Bosses defeated** — count and names (`The Elder ×2` if killed twice).
- **Highest damage dealt** — the player with the most total damage to creatures, bosses included.
- **Kills & deaths** — a monospace table with one row per player, most kills first. Long lists continue in extra fields, up to 60 players.

If the server was off at the due time, the report is posted as soon as it is running again. Players who stay connected across the cut-off are counted as joined in the next period.

**How things are counted**

- *Joins* come from the server's connection list. *Deaths* are reported by the dying player's own game.
- *Mob kills and damage* are measured, like boss damage, by the game that owns the creature (Valheim only applies damage there), from the health each hit actually removes. The killing blow is credited as the kill. So the owner of the creature must run this mod, and kills made by tamed animals, other creatures or the environment are not credited to anyone. Tamed creatures, players and bosses do not count as "mobs"; bosses are counted separately (final phase only for Kall Fimbulbringer).
- Players are identified by character name (case-insensitive).

The running totals are kept in `BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt` (plain text) so a server restart does not lose the week. Delete it to start over.

## Webhook security

The webhooks the **clients upload to** — screenshots, death videos and boss videos — have to be known to every client, because those uploads go directly from the client to Discord. The admin sets them once in the server's config and the server sends them to every connected player, so any player can read them (for example in Configuration Manager). Use dedicated Discord webhooks/channels for them, not your normal chat webhook.

The chat, server-notification, player-notification and weekly-report webhooks are only used by the server. They are never synced, so clients cannot see them.

All of these can only be changed by admins while `[Admin] Server - Lock Configuration` is on (the default). With it off, any player could replace a synced webhook URL.

## Logs and troubleshooting

Everything is written to `BepInEx/cache/ValheimDiscordRelay.log` (kept for 24 hours). Lines are tagged by feature: `[Report]` (weekly report), `[PlayerNotifications]`, `[Death]` (how a death was resolved: hit type, attacker, level, source) and `[DeathVideo]`.

Common causes when nothing arrives in Discord:

- The webhook URL for that feature is empty (the log warns about it; the weekly report warns at most once an hour). For screenshots, death videos and boss videos the URL must be set in the **server's** config, because that value is what clients receive.
- The feature's `Server - Enabled` is off, or on a client that category's `Client - Enabled` is off.
- Death messages: `[Player Deaths] Client - Enabled` is off on that client, or `[Player Deaths] Server - Webhook URL` is empty on the server.
- Boss video: `[Boss Death] Server - Webhook URL` is empty on the server, or `Client - Video Enabled` is off on the client that records it. With `Server - Video Source = Last Attacker`, that is the last attacker's client, and its `[Player Deaths] Client - Video Enabled` must be on too; if it is not, the boss owner's video is used (the log on both sides says which one was chosen).
- Mob kills / damage missing from the weekly report: the player who owned the creature does not have the mod.

