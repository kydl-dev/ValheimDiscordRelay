using System;
using System.Collections;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Tracks local-player hit/death state and decides when a death has occurred.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        /// <summary>
        /// Records the most recent hit taken by the local player, for use as death-cause context.
        /// </summary>
        /// <param name="hit">The hit data reported by Valheim.</param>
        internal void RecordLocalPlayerHit(HitData hit)
        {
            if (hit == null)
                return;

            _lastLocalPlayerHit = hit;
            _lastLocalPlayerHitTime = Time.unscaledTime;

            CaptureAttackerSnapshot(hit);

            Player player = Player.m_localPlayer;
            if (player != null)
            {
                _lastKnownLocalPlayer = player;
                _lastKnownPlayerName = ClientTextUtils.GetLocalPlayerName();
                _haveKnownLocalPlayer = true;
            }
        }

        /// <summary>
        /// Remembers who hit the local player while that attacker is still alive and resolvable, so a
        /// later death (or a damage-over-time tick with no attacker) can still be attributed correctly.
        /// </summary>
        /// <param name="hit">The hit data reported by Valheim.</param>
        private void CaptureAttackerSnapshot(HitData hit)
        {
            try
            {
                if (hit == null || !hit.HaveAttacker() || ZNetScene.instance == null)
                    return;

                Character attacker = hit.GetAttacker();
                if (attacker == null || ReferenceEquals(attacker, Player.m_localPlayer))
                    return;

                AttackerInfo info = AttackerInfo.FromCharacter(attacker, hit.m_attacker);
                if (info != null)
                    _lastAttacker = info;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Debug("Could not snapshot attacker: " + ex.Message);
            }
        }

        /// <summary>
        /// Checks whether the given player's health has reached zero and, if so, handles the death.
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <param name="fromOnDeathSignal">
        /// True when called from Valheim's own Player.OnDeath, at which point Character.m_lastHit is final.
        /// Other triggers (SetHealth, per-frame polling) wait one frame so m_lastHit has settled too.
        /// </param>
        internal void CheckLocalPlayerDeath(Player player, bool fromOnDeathSignal = false)
        {
            if (SessionLifecycle.IsTearingDown)
                return;

            if (player == null || player != Player.m_localPlayer || _deathEventHandledForCurrentPlayer)
                return;

            try
            {
                if (player.GetHealth() > 0f)
                    return;
            }
            catch
            {
                return;
            }

            if (!fromOnDeathSignal)
            {
                int frame = Time.frameCount;
                if (_pendingDeathFrame < 0)
                {
                    _pendingDeathFrame = frame;
                    return;
                }

                if (frame <= _pendingDeathFrame)
                    return;
            }

            HandleDeathIndependentOfHitData(player);
        }

        /// <summary>
        /// Detects local-player identity changes and death across frames.
        /// </summary>
        private void CheckLocalPlayerLifecycle()
        {
            Player current = Player.m_localPlayer;

            if (SessionLifecycle.IsTearingDown)
            {
                bool gameStillShuttingDown = false;

                try
                {
                    gameStillShuttingDown =
                        Game.instance != null && Game.instance.IsShuttingDown();
                }
                catch
                {
                }

                if (current == null || gameStillShuttingDown)
                {
                    _haveKnownLocalPlayer = false;
                    return;
                }

                if (ReferenceEquals(current, _lastKnownLocalPlayer))
                {
                    _haveKnownLocalPlayer = false;
                    return;
                }

                SessionLifecycle.ResetForNewSession();
                _lastKnownLocalPlayer = null;
                _deathEventHandledForCurrentPlayer = false;
                _lastLocalPlayerHit = null;
                _lastLocalPlayerHitTime = -99999f;
                _lastAttacker = null;
                _pendingDeathFrame = -1;
            }

            if (current != null)
            {
                if (!ReferenceEquals(current, _lastKnownLocalPlayer))
                {
                    _lastKnownLocalPlayer = current;
                    _lastKnownPlayerName = ClientTextUtils.GetLocalPlayerName();
                    _haveKnownLocalPlayer = true;
                    _deathEventHandledForCurrentPlayer = false;
                    _lastLocalPlayerHit = null;
                    _lastLocalPlayerHitTime = -99999f;
                    _lastAttacker = null;
                    _pendingDeathFrame = -1;
                }

                CheckLocalPlayerDeath(current);
                return;
            }

            if (Game.instance != null && Game.instance.IsShuttingDown())
            {
                _haveKnownLocalPlayer = false;
                SessionLifecycle.MarkTearingDown("Game.IsShuttingDown");
                return;
            }

            if (_haveKnownLocalPlayer && !_deathEventHandledForCurrentPlayer)
            {
                HandleDeathIndependentOfHitData(null);
                _haveKnownLocalPlayer = false;
            }
        }

        /// <summary>
        /// Handles a detected death using the best available cause context, independent of any HitData event.
        /// </summary>
        /// <param name="player">The player who died, or null if already torn down.</param>
        private void HandleDeathIndependentOfHitData(Player player)
        {
            if (_deathEventHandledForCurrentPlayer)
                return;

            _deathEventHandledForCurrentPlayer = true;

            string playerName = _lastKnownPlayerName;
            if (player != null)
                playerName = ClientTextUtils.GetLocalPlayerName();

            HitData recentHit = _lastLocalPlayerHit;
            DeathMessage deathMessage = DeathMessageBuilder.GetDeathMessage(recentHit, playerName, _lastAttacker);
            NotifyDeath(playerName, deathMessage);
        }

        /// <summary>
        /// How long the in-game death shout is held back. The recorder keeps at most 4 seconds after the death
        /// ([Player Deaths] Client - Video Post Duration is clamped to 2-4), so a shout sent 5 seconds after the death
        /// can never end up in the clip, where it would cover part of the view.
        /// </summary>
        private const float DeathShoutDelaySeconds = 5f;

        private IEnumerator SendDeathShoutDelayed(string gameText)
        {
            float sendAt = Time.unscaledTime + DeathShoutDelaySeconds;

            while (Time.unscaledTime < sendAt)
                yield return null;

            // The player may have quit, or left the world, while the shout was waiting.
            if (QuitRequested || SessionLifecycle.IsTearingDown)
                yield break;

            ClientPlugin.SendDeathShout(gameText);
        }

        /// <summary>
        /// Sends the in-game death shout and starts (or substitutes for) the death-video pipeline.
        /// </summary>
        /// <param name="playerName">The local player's display name.</param>
        /// <param name="message">The generated death-cause message (in-game and Discord forms).</param>
        internal void NotifyDeath(string playerName, DeathMessage message)
        {
            string gameText = message == null ? null : message.Game;
            string deathMessage = DeathMessageBuilder.WrapForDiscord(message == null ? null : message.Discord);

            if (SessionLifecycle.IsTearingDown)
            {
                RelayDiagnostics.Debug(
                    "[DeathVideo] Suppressed death notification during session teardown. " +
                    "Player='" + playerName + "'.");
                return;
            }

            RelayDiagnostics.Info(
                "Death detected. Player='" + playerName + "', Message='" + gameText + "'.");

            // Counted for the weekly report whatever the [Player Deaths] settings are (it has its own switches).
            ValheimDiscordRelay.Report.ReportTracker.OnLocalPlayerDeath(playerName);

            if (!ClientConfig.DeathsActive)
            {
                RelayDiagnostics.Info(
                    "Death detected, but [Player Deaths] Server - Enabled or Client - Enabled is turned off; no shout, message or video will be sent.");
                return;
            }

            _host.StartCoroutine(SendDeathShoutDelayed(gameText));

            if (string.IsNullOrWhiteSpace(ClientConfig.DeathVideoWebhookUrl.Value))
            {
                RelayDiagnostics.Warning(
                    "Death detected, but [Player Deaths] Server - Webhook URL is empty; no video or message will be sent.");
                return;
            }

            if (!ClientConfig.DeathVideoEnabled.Value)
            {
                // Videos are off: still announce the death in Discord, just without a video.
                string messageOnlyPlayer = string.IsNullOrWhiteSpace(playerName) ? "Valheim Player" : playerName;
                string messageOnlyText = string.IsNullOrWhiteSpace(deathMessage)
                    ? DeathMessageBuilder.WrapForDiscord(messageOnlyPlayer.Replace("`", "'") + " got killed by Unknown.")
                    : deathMessage;

                RelayDiagnostics.Info("[Player Deaths] Client - Video Enabled is off; sending the death message without a video.");
                SendDeathMessageOnly(messageOnlyPlayer, messageOnlyText);
                return;
            }

            float now = Time.unscaledTime;
            bool repeatDeath = now - _lastDeathEventTime < RepeatDeathMessageWindowSeconds;
            _lastDeathEventTime = now;

            if (repeatDeath || _deathRecording || _deathProcessing)
            {
                string repeatPlayer = string.IsNullOrWhiteSpace(playerName) ? "Valheim Player" : playerName;
                string repeatPlayerSafe = repeatPlayer.Replace("`", "'");

                // The normal (random) death quote, in its raw Discord form (colour codes intact, not yet wrapped).
                string quote = message == null ? null : message.Discord;
                if (string.IsNullOrWhiteSpace(quote))
                    quote = repeatPlayerSafe + " got killed by Unknown.";

                // Quote, then the "died again" line, then the "no video" line, one per line, in a single code block.
                string repeatMessage =
                    quote.Trim() + "\n" +
                    repeatPlayerSafe + " died again in a very short time, maybe fight an enemy of your size: T.W.I.G." + "\n" +
                    "No video recording of this unfortunate death will be presented.";

                RelayDiagnostics.Info("Repeat death detected within the 30-second death-video window; sending message only.");
                SendDeathMessageOnly(playerName, DeathMessageBuilder.WrapForDiscord(repeatMessage));
                return;
            }

            DebugLog("Death detected. Player='" + playerName + "', Message='" + gameText + "'.");
            DebugLog("Settings: FPS=" + ClientConfig.DeathVideoFps.Value +
                     ", Resolution=" + ClientConfig.DeathVideoResolution.Value +
                     ", Pre=" + ClientConfig.DeathVideoPreDuration.Value +
                     ", Post=" + ClientConfig.DeathVideoPostDuration.Value);

            lock (_sync)
            {
                _deathRecording = true;
                _deathPlayer = string.IsNullOrWhiteSpace(playerName) ? "Valheim Player" : playerName;
                _deathMessage = string.IsNullOrWhiteSpace(deathMessage)
                    ? DeathMessageBuilder.WrapForDiscord(_deathPlayer.Replace("`", "'") + " got killed by Unknown.")
                    : deathMessage;
                _deathTime = Time.unscaledTime;
                _deathQuote = message == null ? null : message.Discord;
                Interlocked.Exchange(ref _deathDelivery, 0);
            }

            _host.StartCoroutine(FinishDeathRecording());
        }
    }
}
