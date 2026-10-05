using System;
using System.Threading;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Game-quit handling for the death and boss video pipelines.
    ///
    /// Quitting (the menu's Quit button, Alt+F4, the window's close button) used to leave the game hanging while a clip
    /// was still being staged, encoded or uploaded: the worker thread sat blocked in img2webp's WaitForExit/ReadToEnd
    /// or in a socket call, and the game cannot finish shutting down while a managed thread is stuck in such a call.
    /// Now the quit event kills the encoder, aborts the upload and sends the Discord message that would have gone out
    /// with the video, with a line saying the player quit before the video was finished.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        /// <summary>Timeout of the final text message. Kept short: the game must not linger on a slow connection.</summary>
        private const int QuitSendTimeoutMilliseconds = 5000;

        /// <summary>How long the quit handler waits for that message (the sender is a background thread).</summary>
        private const int QuitSendWaitMilliseconds = 6000;

        /// <summary>How long it waits for a worker that is already delivering the message itself.</summary>
        private const int QuitWorkerWaitMilliseconds = 3000;

        private bool QuitRequested
        {
            get { return Volatile.Read(ref _quitState) != 0; }
        }

        /// <summary>
        /// Called on the main thread when the application is quitting (Application.quitting / OnApplicationQuit).
        /// Safe to call more than once; only the first call does anything.
        /// </summary>
        internal void OnApplicationQuitting()
        {
            if (Interlocked.Exchange(ref _quitState, 1) != 0)
                return;

            // A boss kill that is still waiting for its video must not lose its announcement. (The Game.Shutdown patch
            // does this too, but Alt+F4 does not necessarily go through Game.Shutdown.) Harmless when nothing is held.
            try
            {
                BossKillTracker.FlushDeferredDeaths();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not flush the pending boss announcement while quitting.", ex);
            }

            bool deathPending = _deathRecording || _deathProcessing;
            bool bossPending = _bossRecording || _bossProcessing;

            if (!deathPending && !bossPending)
                return;

            RelayDiagnostics.Info(
                "The game is quitting while a " + (deathPending ? "death" : "boss") +
                " video is still being processed; cancelling it.");

            // Unblock the worker threads first: kill the encoder, abort the upload. They see QuitRequested and stop.
            WebPTool.CancelActive();
            AbortActiveUploads();

            if (!deathPending)
                return;

            string player;
            string quote;

            lock (_sync)
            {
                player = _deathPlayer;
                quote = _deathQuote;
            }

            if (Interlocked.CompareExchange(ref _deathDelivery, 1, 0) == 0)
            {
                SendQuitDeathMessage(player, quote);
                return;
            }

            // A worker already delivered the message (or is sending it right now). Give it a moment so the process does
            // not exit in the middle of that send.
            SpinWait.SpinUntil(() => !_deathProcessing, QuitWorkerWaitMilliseconds);
        }

        /// <summary>
        /// Sends the death message that was meant to accompany the video, plus a line saying the player quit before the
        /// video finished processing.
        /// </summary>
        /// <param name="player">The player's display name.</param>
        /// <param name="quote">The death-cause line in its raw Discord form, or null.</param>
        private void SendQuitDeathMessage(string player, string quote)
        {
            string name = string.IsNullOrWhiteSpace(player) ? "Valheim Player" : player;
            string safeName = name.Replace("`", "'");

            if (string.IsNullOrWhiteSpace(quote))
                quote = safeName + " got killed by Unknown.";

            // Same shape as the repeat-death message: the normal death line, then the extra line, in one code block.
            string text = DeathMessageBuilder.WrapForDiscord(
                quote.Trim() + "\n" +
                safeName + " quit the game before the death video finished processing, " +
                "so no video recording of this unfortunate death will be presented.");

            string webhookUrl = ClientConfig.DeathVideoWebhookUrl.Value.Trim();
            string username = ClientTextUtils.SanitizeUsername(name);

            // Sent from a background thread so a stalled connection (DNS and connect are not covered by the request
            // timeout) can only delay the quit by QuitSendWaitMilliseconds, never block it.
            Thread sender = new Thread(delegate ()
            {
                try
                {
                    SendWebhookJson(webhookUrl, username, text, QuitSendTimeoutMilliseconds);
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Error("Could not send the death message while quitting.", ex);
                }
            });

            sender.IsBackground = true;
            sender.Name = "ValheimDiscordRelay-QuitSend";
            sender.Start();

            if (!sender.Join(QuitSendWaitMilliseconds))
                RelayDiagnostics.Warning("The death message was still being sent when the quit wait ran out.");
        }
    }
}
