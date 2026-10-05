# Changelog

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
