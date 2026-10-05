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

Every feature that sends something to Discord has its own webhook setting, so you can point each at its own channel.

| Feature | Sent by | Webhook setting | Sent as |
| --- | --- | --- | --- |
| Normal chat | server | `[Chat]` `Normal Webhook URL` (falls back to `Webhook URL`) | the player's name |
| Shouts | server | `[Chat]` `Shout Webhook URL` (falls back to `Webhook URL`) | the player's name |
| Manual screenshots | client | `[Client]` `Screenshot Webhook URL` | the player's name (optional) |
| Death message + video | client | `[Client]` `Death Video Webhook URL` | the player's name |
| Boss announcement | server (embed) + client (video) | `[Boss Death]` `Webhook URL` | `Webhook Username` |
| Server up / down / restart | server | `[Server Notifications]` `Webhook URL` | `Server notifications` (or the webhook's own name) |
| Player joined / left | server | `[Player Notifications]` `Webhook URL` | `Player notifications` |
| Weekly report | server | `[Weekly Report]` `Webhook URL` | `Weekly report` |

## Configuration

The mod uses one shared file:

`BepInEx/config/ValheimDiscordRelay.cfg`

BepInEx writes a description above every setting in that file; the example below is shortened. Sections can appear in any order in the real file.

```ini
## ---- Admin: config lock and delivery logging ----------------------------
[Admin]
Lock Configuration = true
Log Successful Sends = false

## ---- Chat: chat relay (server-side) -------------------------------------
[Chat]
Enabled = true
Webhook URL =
Normal Webhook URL =
Shout Webhook URL =
Max Message Length = 1800
Name Display = NameOnly
Shout Prefix = <ESC>[2;31m
Normal Prefix = <ESC>[2;36m
Queue Limit = 100
Minimum Send Interval Ms = 250

## ---- Server up / down / restart -------------------------------------------
[Server Notifications]
Enabled = true
Webhook URL =
Use Discord Webhook Name = false
Server Up = true
Server Down = true
Server Restart = true
Restart Window Seconds = 120

## ---- Player joined / left -------------------------------------------------
[Player Notifications]
Enabled = true
Webhook URL =
Arrival Avatar URL =
Leave Avatar URL =

## ---- Boss kills -----------------------------------------------------------
[Boss Death]
Enabled = true
Webhook URL =
Webhook Username = Boss Death Announcement!
Video Source = Boss Owner
Video Enabled = true
Video FPS = 30
Video Resolution = 960x540
Video Pre Duration = 4
Video Post Duration = 4

## ---- Weekly report --------------------------------------------------------
[Weekly Report]
Enabled = true
Webhook URL =
Report Day = Sunday
Report Time = 20:00
Send Test Report = false

## ---- Client features (every player). The two webhook URLs here are set on the
## ---- SERVER by the admin and synced to every client.
[Client]
Enabled = true
Max Message Length = 1000
Screenshot Key = PageUp
Screenshot Webhook URL =
Include Player Name = true
Death Video Enabled = true
Death Video FPS = 30
Death Video Resolution = 960x540
Death Video Pre Duration = 4
Death Video Post Duration = 4
Death Video Webhook URL =
```

`<ESC>` stands for the real escape character (ASCII 27) that BepInEx writes into the file for the two ANSI prefixes; leave those two settings alone unless you want other colours.

### What is synced and who can change it

The mod uses ServerSync. The server's values for the settings in the middle column are sent to every connected player, and a change made in-game (Configuration Manager, F1) by an admin flows back to the server, which saves it. **Webhooks are set by the admin, on the server.** Players do not enter their own: a client's own value for a synced setting is replaced by the server's while connected.

| Section | Synced (admin-controlled) | Local only |
| --- | --- | --- |
| `[Admin]` | `Lock Configuration`, `Log Successful Sends` | nothing |
| `[Chat]` | `Enabled`, `Max Message Length`, `Name Display`, `Shout Prefix`, `Normal Prefix`, `Queue Limit`, `Minimum Send Interval Ms` | the three chat webhook URLs (server only, never sent to clients) |
| `[Server Notifications]` | `Use Discord Webhook Name` | `Webhook URL` (server only) and everything else |
| `[Player Notifications]` | `Enabled`, `Arrival Avatar URL`, `Leave Avatar URL` | `Webhook URL` (server only) |
| `[Boss Death]` | `Enabled`, `Webhook Username`, `Video Source`, **`Webhook URL`** | the `Video ...` settings |
| `[Weekly Report]` | `Enabled`, **`Report Day`, `Report Time`, `Send Test Report`** | `Webhook URL` (server only) |
| `[Client]` | **`Screenshot Webhook URL`, `Death Video Webhook URL`** | everything else |

In short:

- **Webhooks only the server uses** (chat, server notifications, player notifications, weekly report) live in the server's config only. They are never sent to clients, so no client can see or change them.
- **Webhooks the clients upload to** (screenshots, death videos, boss videos) are set by the admin in the *server's* config — including the `[Client]` section there — and synced to every client. Players cannot set their own.
- **The weekly report schedule and the test trigger** are synced and admin-controlled.

### `[Admin]`

- `Lock Configuration` (default `true`): only players listed in the server's `adminlist.txt` can change the synced settings, which includes all of the above. Set it to `false` to let ANY connected player change them — webhook URLs and the report schedule included — so leave it on for a public server.
- `Log Successful Sends` (default `false`) — log every successful Discord delivery from every category: chat, server notifications, player notifications, boss deaths and the weekly report (in the server's log), and screenshot / death video / boss video uploads (in the log of the game that uploaded them).

### `[Chat]`

- `Enabled` — enable the server-side chat relay.
- `Webhook URL` — fallback Discord webhook, used when the specific Normal/Shout webhook is empty.
- `Normal Webhook URL` — webhook for normal chat; falls back to `Webhook URL`.
- `Shout Webhook URL` — webhook for shouts; falls back to `Webhook URL`.
- `Max Message Length` — maximum chat text sent to Discord (100–1900).
- `Name Display` — the Discord username format: `NameOnly` (`Bjorn`), `NameWithNumber` (`Bjorn [1]`, `Bjorn [2]` for duplicate character names) or `NameWithIdSuffix` (`Bjorn [5678]`, the last 4 digits of the platform ID). Default `NameOnly`.
- `Shout Prefix` — ANSI prefix for shouts; default is dim red.
- `Normal Prefix` — ANSI prefix for normal chat; default is dim cyan.
- `Queue Limit` — maximum queued Discord messages; the oldest are dropped when it is full (10–1000).
- `Minimum Send Interval Ms` — pacing between webhook requests (50–5000).

### `[Server Notifications]`

- `Enabled` — enable server up/down/restart notifications.
- `Webhook URL` — the webhook for them.
- `Use Discord Webhook Name` — unchecked: messages are sent as **Server notifications**. Checked: no username is sent, so Discord shows the name set on the webhook itself.
- `Server Up`, `Server Down`, `Server Restart` — turn each message on or off.
- `Restart Window Seconds` — a startup within this many seconds of a clean shutdown (including boot and world load) is reported as a restart; a later one as "is up" (10–3600, default 120).

### `[Player Notifications]`

- `Enabled`, `Webhook URL`, `Arrival Avatar URL`, `Leave Avatar URL` — see [Player notifications](#player-notifications).

### `[Boss Death]`

- `Enabled` — announce boss deaths.
- `Webhook URL` — the boss webhook. The server posts the embed to it, and the player whose game owns the boss uploads the video to it. Set it once on the server; it is synced to every client.
- `Webhook Username` — name shown as the sender of both messages; default `Boss Death Announcement!`.
- `Video Source` — whose game records the boss video: `Boss Owner` (default) or `Last Attacker`. Synced from the server and admin-controlled. **With `Last Attacker` the video is recorded by that player's own game, so that player's `[Client] Death Video FPS` and `Death Video Resolution` apply (and `[Client] Death Video Enabled` must be on), not the boss owner's and not the admin's.** See [Boss death announcements](#boss-death-announcements).
- `Video Enabled` — upload the boss death video (on the game that records it).
- `Video FPS` (`20` or `30`), `Video Resolution` (`480x270`, `640x360`, `960x540`) — only used when `[Client] Death Video Enabled` is off (see [Boss death announcements](#boss-death-announcements)); never used for a `Last Attacker` video.
- `Video Pre Duration`, `Video Post Duration` — `2` to `4` seconds before / after the boss dies (the recording player's own values).

### `[Weekly Report]`

- `Enabled`, `Webhook URL`, `Report Day`, `Report Time`, `Send Test Report` — see [Weekly report](#weekly-report).

### `[Client]`

- `Enabled` — enable local chat interception and client capture features.
- `Max Message Length` — maximum chat text sent from the client to the server (1–1000).
- `Screenshot Key` — screenshot hotkey; default `PageUp`. Set to `None` to disable.
- `Screenshot Webhook URL` — Discord webhook for manual screenshots. Set by the admin in the server's config and synced to every client.
- `Include Player Name` — use the local Valheim character name as the Discord username for manual screenshots.
- `Death Video Enabled` — enable automatic death videos **and the Discord death message** (see [Death messages](#death-messages)).
- `Death Video FPS` — `20` or `30`.
- `Death Video Resolution` — `480x270`, `640x360` or `960x540`.
- `Death Video Pre Duration` / `Death Video Post Duration` — `2` to `4` seconds before / after death.
- `Death Video Webhook URL` — Discord webhook for death videos and the death message. Set by the admin in the server's config and synced to every client.

The default death-video configuration is **30 FPS, 960x540, 4 seconds before death and 4 seconds after death**.

## Chat relay

The client sends only the chat type and text; the server resolves the sender (character name, platform identifier) from the actual network peer. No Discord account linking is used.

Normal chat uses dim cyan ANSI formatting (`ESC[2;36m`); shouts use dim red (`ESC[2;31m`). Whisper/private chat is intentionally not relayed.

The server delivers webhooks sequentially, with queueing, pacing, and retry handling for Discord rate limits and temporary HTTP errors.

Valheim's own "I have arrived!" shout is a special case: it is dropped while `[Player Notifications] Enabled` is on, and relayed like any other shout when it is off. See [Player notifications](#player-notifications).

Death messages are shouted in game chat, but that shout is **not** sent to the normal Discord chat webhook; Discord gets the death message through the death webhook instead.

## Manual screenshots

Press `PageUp` by default to capture the final rendered Valheim frame, including UI from other mods. Screenshots are encoded as PNG and uploaded directly from the client — they do not pass through the Valheim server. The temporary local file is deleted after upload.

## Death messages

When the local player dies, the client works out what killed them from Valheim's own hit data, picks a random message for that kind of death, and:

1. **shouts it in game chat** (so everybody in the world sees it; the shout is not relayed to the chat webhook), and
2. **sends it to Discord** through `[Client] Death Video Webhook URL`, together with the death video (or alone, see below).

The Discord part needs `[Client] Death Video Enabled` on **and** a `Death Video Webhook URL`; if either is missing the shout still happens and the log says why nothing was sent to Discord. Deaths are also counted for the [weekly report](#weekly-report) whatever these two settings are.

### How the killer is found

- The attacker (name, level, boss flag, prefab) is snapshotted when it hits you, so it is still named correctly if it dies or despawns before you do.
- Burning and poison deaths whose final tick has no attacker are credited to the creature that hit you within the previous 5 seconds.
- Creature level is shown as stars: none for level 1, one ⭐ for level 2, two ⭐⭐ for level 3, and so on up to five. In Discord they are emoji; in in-game chat they are written out — `(1 star)`, `(2 stars)` — because Valheim's font cannot draw emoji.
- Animals are recognised by prefab name: Boar, Neck, Bjorn (the bear), Serpent, Leech, Wolf, Bat, Bat_swamp, Lox, Deathsquito, Volture, Asksvin, Moose. To count another creature as an animal, add its prefab name to `AnimalPrefabs` in `Client/DeathQuotes.cs`.

### Quote lists

The messages are picked at random (never the same one twice in a row within a list) from lists in **`Client/DeathQuotes.cs`**, which is the file to edit to add or reword quotes.

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
- **Repeat deaths:** if the player dies again while the previous death is still being recorded or encoded (or within 30 seconds of it), no second video is made; Discord gets a short message instead: `{Player} seems to be weak, they died again. Try sparring with the dummy!`

### Discord formatting

Every Discord death message is sent inside a code block. When the enemy name carries a colour tag, the block is an `ansi` block and the name is red; otherwise it is a plain block. The upload is sent as the player's name.

## Death videos

When the local player dies, the client records the configured period immediately before and after the death and uploads the result as an animated WebP, directly from the client, to the configured `Death Video Webhook URL`. Each upload carries the death message from [above](#death-messages).

The rolling pre/post-death frame buffer is kept in memory, not on disk. Disk is only touched once a death actually happens, to write the relevant frames out for `img2webp` (an external process, so it needs real files) — those staged frames and old WebP outputs are then swept automatically (a background check every 5 minutes removes anything older than 24 hours).

WebP quality/compression is chosen automatically and isn't user-configurable: failed attempts retry internally with more compact settings, and if every attempt fails, the client sends only the death message as text. Frames are staged under a short path in the system temp directory (`%TEMP%\ValheimDiscordRelay\DeathEncoding\...`) rather than the full Gale/BepInEx cache path, to keep the `img2webp` command line short.

## Boss death announcements

Use `[Boss Death]` for boss kill announcements. When a boss dies, the server posts an embed to the boss webhook, and — at the same time, as a separate message — the player whose game owned the boss (or, with `Video Source = Last Attacker`, the player who landed the last hit) uploads a short video of the moment it died.

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

**The video.** One game records and uploads it, chosen by `Video Source` (below). By default (`Boss Owner`) that is the client that owns the boss when it dies (that is normally someone near it). Its pre and post durations are `Video Pre Duration` / `Video Post Duration`.

- While `[Client] Death Video Enabled` is **on**, the boss video is cut from the frames the player-death recorder is already capturing, so it uses the **death video's FPS and resolution** and a boss fight costs no extra screen capture or encoding.
- While it is **off**, a separate recorder runs only while that client is fighting a boss and uses `Video FPS` and `Video Resolution`.

When a video is going to be uploaded, the announcement embed is held back until the video has been encoded and is about to upload, so the two appear in Discord together (embed first, the video as soon as its upload finishes). If the video is not ready within 75 seconds, or you leave the game first, the embed is sent anyway. If that client has no `Webhook URL` set, or `Video Enabled` is off, no video is uploaded and the embed is posted immediately by the server.

**`Video Source`: Boss Owner or Last Attacker.** The boss owner is whoever's game happens to own the boss when it dies, and that player can be facing away from it, which gives a video of nothing. `Last Attacker` records the video on the game of the player who landed the last hit instead, who is almost always looking at the boss. It is off by default (`Boss Owner`) and is a synced, admin-controlled setting.

- **The recording player's settings apply, not the admin's and not the boss owner's:** `[Client] Death Video FPS` and `Death Video Resolution`, `Video Pre Duration` / `Video Post Duration`, and `Video Enabled`. The boss video is cut from the death-recorder frames, so `[Client] Death Video Enabled` has to be **on** for that player; otherwise their game declines.
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
- Messages are sent with the username **Server notifications**. Tick `Use Discord Webhook Name` to send no username at all, so Discord shows the name set on the webhook itself (Channel settings → Integrations → Webhooks).
- A clean shutdown followed by a startup within `Restart Window Seconds` is reported as a restart; a longer gap as "is up". The "down" message is sent synchronously so it is not lost when the process exits.

## Player notifications

`[Player Notifications]` has its own webhook, and posts when someone joins or leaves, sent as **Player notifications**:

```text
{Player.name} has joined the world. Population: {players.count}. Good luck!
{Player.name} had to leave us. Hope they had fun. Population: {players.count}.
```

`Population` is the number of players online after the join / leave (the host of a game-hosted world counts too).

- `Enabled` (synced, admin-controlled).
  - **On:** Valheim's own arrival message ("I have arrived!") is intercepted on the client and ignored — it is **not** sent to any Discord webhook. The mod posts the messages above instead.
  - **Off:** nothing is intercepted. The game's arrival message is relayed like any other shout through the normal chat webhook, and no join/leave messages are posted.
- `Webhook URL` — the webhook for these messages. Server-side only; never sent to clients.
- `Arrival Avatar URL` / `Leave Avatar URL` — optional http/https image URLs used as the webhook icon for join / leave messages. Leave one empty to use the webhook's default Discord icon. Both are synced and admin-controlled.

**How joins and leaves are detected.** Valheim sends "I have arrived!" on *every* spawn (including after each death) and sends **no message at all when a player leaves** — the game has no such text. So the server watches the connected players instead: one join per connection, one leave per disconnect, nothing on respawn. A server shutdown is not announced as everybody leaving.

## Weekly report

`[Weekly Report]` posts one embed per week through its own webhook, sent as **Weekly report**. Its code lives in its own `Report/` folder.

- `Enabled` — synced from the server. Clients only collect and send numbers while it is on.
- `Webhook URL` — the report webhook. Server-side only; never sent to clients.
- `Report Day` — default `Sunday`. Synced; only admins can change it (while `Lock Configuration` is on).
- `Report Time` — `HH:mm`, in the server's local time zone; default `20:00`. Synced; only admins can change it.
- `Send Test Report` — debug helper. Set to `true` to post the report as it stands right now, marked "(test)". It does not reset the statistics and turns itself back to `false`. It fires when an admin changes the setting (Configuration Manager, or a live config reload on the server) or at the next server start. Synced; only admins can trigger it, and clients never act on it.

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

All of these can only be changed by admins while `Lock Configuration` is on (the default). With it off, any player could replace a synced webhook URL.

## Logs and troubleshooting

Everything is written to `BepInEx/cache/ValheimDiscordRelay.log` (kept for 24 hours). Lines are tagged by feature: `[Report]` (weekly report), `[PlayerNotifications]`, `[Death]` (how a death was resolved: hit type, attacker, level, source) and `[DeathVideo]`.

Common causes when nothing arrives in Discord:

- The webhook URL for that feature is empty (the log warns about it; the weekly report warns at most once an hour). For screenshots, death videos and boss videos the URL must be set in the **server's** config, because that value is what clients receive.
- The feature's `Enabled` is off, or on a client `[Client] Enabled` is off.
- Death messages: `[Client] Death Video Enabled` is off on that client, or `Death Video Webhook URL` is empty on the server.
- Boss video: `[Boss Death] Webhook URL` is empty on the server, or `Video Enabled` is off on the client that records it. With `Video Source = Last Attacker`, that is the last attacker's client, and its `[Client] Death Video Enabled` must be on too; if it is not, the boss owner's video is used (the log on both sides says which one was chosen).
- Mob kills / damage missing from the weekly report: the player who owned the creature does not have the mod.

## Project layout

| Folder | Contents |
| --- | --- |
| `Client/` | chat interception, screenshots, death detection, death messages and quotes, death/boss videos |
| `Server/` | chat relay, webhook queue, server notifications, player notifications, boss announcements and quotes |
| `Report/` | the weekly report: config, data store, scheduler, embed builder, collection hooks |
| `Common/` | shared code: protocol, config sync, boss catalog, logging |
| `Build/` | WebP tool preparation and release build scripts |

Building from source needs the DLLs listed in `ValheimDiscordRelay.csproj` in `References/` (including `UnityEngine.ScreenCaptureModule.dll`).
