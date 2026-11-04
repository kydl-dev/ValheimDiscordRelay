using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Manual clips: press the hotkey and the last few seconds plus the next few seconds are sent to Discord as an
/// animated WebP, under the sender name "ValheimRelay Clips".
///
/// Like the boss video, a clip does NOT capture anything itself. The rolling frame buffer (see
/// ClientCaptureService.RollingBuffer.cs) already holds the last ~4 seconds, so on the key press the clip only
/// remembers its time window, waits for the "post" part to be recorded, and then copies that window out of the
/// buffer. The frames are shared by reference, never removed, so a death or boss video in the same moment is not
/// affected. While a clip waits for its post part, pruning of the buffer is paused (see CaptureRollingFrame).
///
/// The buffer's frame rate and resolution are the ones of [Player Deaths] Client - Video FPS / Resolution.
/// The buffer runs whenever death videos are on OR clips are ready (see <see cref="RollingBufferWanted"/>), so clips
/// work with death videos switched off.
///
/// One clip at a time: a key press while the previous clip is still being recorded or sent is ignored.
/// </summary>
internal sealed partial class ClientCaptureService
{
    /// <summary>True from the key press until the post part has been recorded (frames must not be pruned meanwhile).</summary>
    private volatile bool _clipRecording;

    /// <summary>True while the clip is being staged / encoded / uploaded on a worker thread.</summary>
    private volatile bool _clipProcessing;

    /// <summary>The clip's time window, in Time.unscaledTime. Main thread only.</summary>
    private float _clipStart;
    private float _clipEnd;

    private string _clipPlayerName;
    private bool _warnedClipWebhookEmpty;

    /// <summary>Text for the in-game notice; written by the worker thread, shown by the main thread (see ShowPendingClipNotice).</summary>
    private string _pendingClipNotice;

    /// <summary>Set once Discord has refused the normal sender name and accepted the fallback; later clips go straight to the fallback.</summary>
    private static volatile bool _clipSenderNameRejected;

    private const int ClipMaxUploadBytes = 20 * 1024 * 1024;

    /// <summary>
    /// True when the rolling frame buffer must be running: for death videos, or because a clip can be taken.
    /// </summary>
    private static bool RollingBufferWanted
    {
        get { return ClientConfig.DeathVideoActive || ClipConfig.Ready; }
    }

    private static void ClipLog(string message)
    {
        RelayDiagnostics.Debug("[Clip] " + message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Hotkey
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Called every frame from Tick(): shows a finished clip's in-game notice and starts a new clip on the hotkey.
    /// </summary>
    private void TickClip()
    {
        ShowPendingClipNotice();

        if (!ClipConfig.Active)
            return;

        if (!IsHotkeyComboDown(ClipConfig.Key.Value))
            return;

        // A letter key must not start a clip while the player is typing (chat, console, sign / map pin text).
        if (ClientTextUtils.IsTypingInGame())
            return;

        BeginClip();
    }

    /// <summary>
    /// True on the frame the shortcut is completed: every key of it is held and at least one of them went down in this
    /// frame. So "F9 + LeftControl" works whichever key is pressed first, and keys that are not part of the shortcut are
    /// ignored (walking, sprinting). Single-key shortcuts behave like a normal key press.
    /// </summary>
    private static bool IsHotkeyComboDown(KeyboardShortcut shortcut)
    {
        KeyCode mainKey = shortcut.MainKey;
        if (mainKey == KeyCode.None)
            return false;

        bool allHeld = Input.GetKey(mainKey);
        bool anyWentDown = Input.GetKeyDown(mainKey);

        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
                allHeld = false;

            if (Input.GetKeyDown(modifier))
                anyWentDown = true;
        }

        return allHeld && anyWentDown;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Recording window
    // ---------------------------------------------------------------------------------------------------------

    private void BeginClip()
    {
        if (QuitRequested)
            return;

        if (string.IsNullOrWhiteSpace(ClipConfig.WebhookUrl.Value))
        {
            if (!_warnedClipWebhookEmpty)
            {
                _warnedClipWebhookEmpty = true;
                RelayDiagnostics.Warning("Clip requested, but [Clips] Server - Webhook URL is empty.");
            }

            return;
        }

        if (_clipRecording || _clipProcessing)
        {
            RelayDiagnostics.Info("Clip hotkey pressed while the previous clip is still being recorded or sent; ignored.");
            return;
        }

        int pre = Mathf.Clamp(ClipConfig.PreDuration.Value, 2, 4);
        int post = Mathf.Clamp(ClipConfig.PostDuration.Value, 2, 4);
        float now = Time.unscaledTime;

        // The name is read now, on the main thread. After a death the local player does not exist for a moment.
        string playerName = null;
        if (ClipConfig.IncludePlayerName.Value)
            playerName = Player.m_localPlayer != null ? ClientTextUtils.GetLocalPlayerName() : _lastKnownPlayerName;

        _clipStart = now - pre;
        _clipEnd = now + post;
        _clipPlayerName = playerName;
        _clipRecording = true;

        RelayDiagnostics.Info("Clip hotkey pressed; saving the last " + pre + " s and the next " + post + " s.");

        _host.StartCoroutine(FinishClipRecording());
    }

    private IEnumerator FinishClipRecording()
    {
        float start = _clipStart;
        float end = _clipEnd;
        string playerName = _clipPlayerName;

        while (Time.unscaledTime < end)
            yield return null;

        // Processing is switched on before recording is switched off, so there is never a moment when neither is set.
        _clipProcessing = true;
        _clipRecording = false;

        List<FrameEntry> selected = new List<FrameEntry>();

        // Copy, never remove (see the class comment).
        lock (_sync)
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                FrameEntry frame = _frames[i];
                if (frame.Time >= start && frame.Time <= end)
                    selected.Add(frame);
            }

            selected.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        ClipLog("Clip window complete. Selected " + selected.Count + " frames.");

        if (selected.Count == 0)
        {
            RelayDiagnostics.Warning(
                "No frames were buffered for the clip; nothing was uploaded. " +
                "(The frame buffer only runs while [Clips] is enabled with a webhook, or death videos are on.)");

            SetClipNotice("Clip could not be sent: nothing was recorded.");
            _clipProcessing = false;
            yield break;
        }

        // Everything the worker thread needs is read here, on the main thread.
        string webhookUrl = ClipConfig.WebhookUrl.Value.Trim();
        int fps = Mathf.Clamp(ClientConfig.DeathVideoFps.Value, 20, 30);
        string content = string.IsNullOrWhiteSpace(playerName) ? null : playerName + " saved a clip.";

        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                List<string> staged = StageDeathFrames(selected, "clip-");
                ClipLog("Frame staging complete. Staged " + staged.Count + " of " + selected.Count + " frames.");
                ProcessClipVideo(webhookUrl, content, fps, staged);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Clip processing failed.", ex);
                _clipProcessing = false;
            }
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Encode + upload (worker thread)
    // ---------------------------------------------------------------------------------------------------------

    private void ProcessClipVideo(string webhookUrl, string content, int fps, List<string> frames)
    {
        string output = null;
        bool uploaded = false;

        try
        {
            if (frames == null || frames.Count == 0)
            {
                RelayDiagnostics.Warning("No frames could be staged for the clip; nothing was uploaded.");
                return;
            }

            frames.Sort(StringComparer.OrdinalIgnoreCase);

            string outputDirectory = Path.GetDirectoryName(frames[0]);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = _encodingRootDirectory;

            output = Path.Combine(outputDirectory, "clip-" + DateTime.UtcNow.ToString("HHmmss") + ".webp");

            if (QuitRequested)
            {
                ClipLog("Clip skipped: the game is quitting.");
                return;
            }

            if (!WebPTool.IsAvailable())
                throw new InvalidOperationException("Embedded WebP encoder is not available. " + WebPTool.GetStatus());

            int[] qualities = new[] { 80, 65, 50, 35 };
            int[] methods = new[] { 6, 5, 4, 3 };

            for (int i = 0; i < qualities.Length && !uploaded; i++)
            {
                if (QuitRequested)
                    break;

                ClipLog("WebP attempt " + (i + 1) + "/4: quality=" + qualities[i] + ", method=" + methods[i] + ".");
                SafeDelete(output);

                if (!WebPTool.EncodeAnimation(frames, output, fps, qualities[i], methods[i]))
                {
                    ClipLog("WebP attempt " + (i + 1) + " failed during encoding.");
                    continue;
                }

                if (!File.Exists(output))
                {
                    ClipLog("WebP attempt " + (i + 1) + " reported success but output file was missing.");
                    continue;
                }

                long size = new FileInfo(output).Length;
                ClipLog("WebP attempt " + (i + 1) + " produced " + size + " bytes.");

                if (size > ClipMaxUploadBytes)
                {
                    ClipLog("WebP attempt " + (i + 1) + " exceeded 20 MiB and was rejected.");
                    continue;
                }

                bool tooLarge;
                byte[] fileBytes = File.ReadAllBytes(output);
                string clipFileName = Path.GetFileName(output);

                if (UploadWithRetry(() => SendClipFile(webhookUrl, clipFileName, fileBytes, content), out tooLarge))
                {
                    uploaded = true;
                    ClipLog("Clip uploaded successfully.");
                }
                else if (tooLarge)
                {
                    ClipLog("WebP attempt " + (i + 1) + " was too large for Discord; trying a smaller encode.");
                }
                else
                {
                    ClipLog("Clip upload failed; not re-encoding (the file was not the problem).");
                    break;
                }
            }

            if (!uploaded && QuitRequested)
                ClipLog("The clip was abandoned: the game is quitting.");
            else if (!uploaded)
                RelayDiagnostics.Warning("The clip could not be encoded or uploaded.");
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Clip processing failed.", ex);
        }
        finally
        {
            SafeDelete(output);

            if (frames != null)
            {
                foreach (string frame in frames)
                    SafeDelete(frame);

                DeleteClipStagingDirectory(frames);
            }

            if (!QuitRequested)
                SetClipNotice(uploaded ? "Clip sent to Discord." : "Clip could not be sent. See the Valheim Discord Relay log.");

            _clipProcessing = false;
        }
    }

    /// <summary>
    /// Uploads the clip as "ValheimRelay Clips". If Discord answers HTTP 400 (for example because it does not
    /// accept that sender name), the same upload is repeated once under <see cref="ClipConfig.FallbackSenderName"/>,
    /// and that name is then used for every later clip.
    /// </summary>
    internal static void SendClipFile(string webhookUrl, string filename, byte[] file, string content)
    {
        string sender = _clipSenderNameRejected ? ClipConfig.FallbackSenderName : ClipConfig.SenderName;

        try
        {
            SendWebhookFile(webhookUrl, sender, filename, "image/webp", file, content);
        }
        catch (WebException ex) when (!_clipSenderNameRejected && IsBadRequest(ex))
        {
            RelayDiagnostics.Warning(
                "Discord answered HTTP 400 for the clip sender name '" + ClipConfig.SenderName +
                "'; retrying once as '" + ClipConfig.FallbackSenderName + "'.");

            SendWebhookFile(webhookUrl, ClipConfig.FallbackSenderName, filename, "image/webp", file, content);
            _clipSenderNameRejected = true;
        }
    }

    private static bool IsBadRequest(WebException ex)
    {
        HttpWebResponse response = ex.Response as HttpWebResponse;
        return response != null && response.StatusCode == HttpStatusCode.BadRequest;
    }

    private void DeleteClipStagingDirectory(List<string> frames)
    {
        try
        {
            if (frames.Count == 0)
                return;

            string stagingDirectory = Path.GetDirectoryName(frames[0]);
            if (!string.IsNullOrWhiteSpace(stagingDirectory) &&
                Directory.Exists(stagingDirectory) &&
                !string.IsNullOrWhiteSpace(_encodingRootDirectory) &&
                stagingDirectory.IndexOf(Path.Combine(_encodingRootDirectory, "DeathEncoding"), StringComparison.OrdinalIgnoreCase) == 0)
            {
                Directory.Delete(stagingDirectory, true);
            }
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not remove temporary clip encoding directory.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // In-game notice
    //
    // Shown when the clip is finished, never when the key is pressed: a message at the moment of the press would sit in
    // the top-left corner of the very footage that is still being recorded.
    // ---------------------------------------------------------------------------------------------------------

    private void SetClipNotice(string text)
    {
        Interlocked.Exchange(ref _pendingClipNotice, text);
    }

    private void ShowPendingClipNotice()
    {
        if (Volatile.Read(ref _pendingClipNotice) == null)
            return;

        string notice = Interlocked.Exchange(ref _pendingClipNotice, null);
        if (string.IsNullOrEmpty(notice))
            return;

        try
        {
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, notice);
        }
        catch (Exception ex)
        {
            ClipLog("Could not show the in-game notice: " + ex.Message);
        }
    }

    private void DisposeClip()
    {
        _clipRecording = false;
        _clipProcessing = false;
        Interlocked.Exchange(ref _pendingClipNotice, null);
    }
}
