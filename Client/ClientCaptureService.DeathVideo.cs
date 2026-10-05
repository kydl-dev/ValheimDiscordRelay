using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Turns the staged frames from a death into a WebP upload, with a text-only fallback.
    /// </summary>
    internal sealed partial class ClientCaptureService
    {
        private IEnumerator FinishDeathRecording()
        {
            float postEnd = _deathTime + Mathf.Clamp(ClientConfig.DeathVideoPostDuration.Value, 2, 4);

            while (Time.unscaledTime < postEnd)
                yield return null;

            _deathRecording = false;
            _deathProcessing = true;

            string player;
            string deathMessage;
            List<FrameEntry> selected = new List<FrameEntry>();

            lock (_sync)
            {
                player = _deathPlayer;
                deathMessage = _deathMessage;

                float start = _deathTime - Mathf.Clamp(ClientConfig.DeathVideoPreDuration.Value, 2, 4);
                float end = postEnd;

                // Copy, never remove: the rolling buffer keeps running and a boss clip may need the very same frames
                // (FrameEntry bytes are immutable after capture, so sharing the references costs no memory). The normal
                // rolling prune drops them once they are old enough.
                for (int i = 0; i < _frames.Count; i++)
                {
                    FrameEntry frame = _frames[i];
                    if (frame.Time >= start && frame.Time <= end)
                        selected.Add(frame);
                }

                selected.Sort((a, b) => a.Time.CompareTo(b.Time));
            }

            DebugLog("Death capture complete. Selected " + selected.Count + " frames.");

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> stagedFrames = StageDeathFrames(selected);
                DebugLog("Death frame staging complete. Staged " + stagedFrames.Count + " of " + selected.Count + " frames.");
                ProcessDeathVideo(player, deathMessage, stagedFrames);
            });
        }

        private List<string> StageDeathFrames(List<FrameEntry> frames, string directoryPrefix = "")
        {
            List<string> staged = new List<string>();
            string stagingDirectory = Path.Combine(
                _encodingRootDirectory,
                "DeathEncoding",
                directoryPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));

            try
            {
                Directory.CreateDirectory(stagingDirectory);

                for (int i = 0; i < frames.Count; i++)
                {
                    if (QuitRequested)
                    {
                        DebugLog("Frame staging stopped: the game is quitting.");
                        break;
                    }

                    string destination = Path.Combine(
                        stagingDirectory,
                        "frame-" + i.ToString("D5") + ".jpg");

                    try
                    {
                        byte[] data = frames[i].Data;
                        if (data == null || data.Length == 0)
                        {
                            RelayDiagnostics.Error("Buffered death frame was empty; skipping.");
                            DebugLog("ERROR: Buffered death frame was empty; skipping.");
                            continue;
                        }

                        File.WriteAllBytes(destination, data);
                        staged.Add(destination);
                    }
                    catch (Exception ex)
                    {
                        RelayDiagnostics.Error("Could not stage death frame '" + destination + "'.", ex);
                        DebugLog("ERROR: Could not stage death frame '" + destination + "': " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not create death-frame staging directory.", ex);
                DebugLog("ERROR: Could not create death-frame staging directory: " + ex);
            }

            return staged;
        }

        private void ProcessDeathVideo(string player, string deathMessage, List<string> frames)
        {
            DebugLog("Beginning WebP processing. Player='" + player + "', Message='" + deathMessage +
                     "', Frames=" + (frames == null ? 0 : frames.Count) + ".");

            if (frames == null || frames.Count == 0)
            {
                SendDeathMessageOnlyOnce(player, deathMessage);
                _deathProcessing = false;
                return;
            }

            frames.Sort(StringComparer.OrdinalIgnoreCase);

            string outputDirectory = frames.Count > 0 ? Path.GetDirectoryName(frames[0]) : null;
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = _encodingRootDirectory;

            string output = Path.Combine(
                outputDirectory, player + "-death-" + DateTime.UtcNow.ToString("HHmmss") + ".webp");

            try
            {
                if (QuitRequested)
                {
                    DebugLog("Death video skipped: the game is quitting.");
                    return;
                }

                if (!WebPTool.IsAvailable())
                    throw new InvalidOperationException("Embedded WebP encoder is not available. " + WebPTool.GetStatus());

                string deathText = deathMessage;
                bool uploaded = false;

                int[] qualities = new[] { 80, 65, 50, 35 };
                int[] methods = new[] { 6, 5, 4, 3 };

                for (int i = 0; i < qualities.Length && !uploaded; i++)
                {
                    if (QuitRequested)
                        break;

                    DebugLog("WebP attempt " + (i + 1) + "/4: quality=" + qualities[i] + ", method=" + methods[i] + ".");
                    SafeDelete(output);

                    if (!WebPTool.EncodeAnimation(
                        frames,
                        output,
                        Mathf.Clamp(ClientConfig.DeathVideoFps.Value, 20, 30),
                        qualities[i],
                        methods[i]))
                    {
                        DebugLog("WebP attempt " + (i + 1) + " failed during encoding.");
                        continue;
                    }

                    if (!File.Exists(output))
                    {
                        DebugLog("WebP attempt " + (i + 1) + " reported success but output file was missing.");
                        continue;
                    }

                    long size = new FileInfo(output).Length;
                    DebugLog("WebP attempt " + (i + 1) + " produced " + size + " bytes.");

                    if (size > 20L * 1024L * 1024L)
                    {
                        DebugLog("WebP attempt " + (i + 1) + " exceeded 20 MiB and was rejected.");
                        continue;
                    }

                    try
                    {
                        SendWebhookFile(
                            ClientConfig.DeathVideoWebhookUrl.Value.Trim(),
                            ClientTextUtils.SanitizeUsername(player),
                            Path.GetFileName(output),
                            "image/webp",
                            File.ReadAllBytes(output),
                            deathText);

                        uploaded = true;
                        Interlocked.Exchange(ref _deathDelivery, 1);
                        DebugLog("Death WebP uploaded successfully.");
                    }
                    catch (Exception ex)
                    {
                        DebugLog("Death WebP upload attempt failed: " + ex);
                        RelayDiagnostics.Warning(
                            "Death WebP upload attempt failed (" + (i + 1) + "/4): " + ex.Message);
                    }
                }

                SafeDelete(output);

                if (!uploaded)
                {
                    if (QuitRequested)
                    {
                        // The quit handler sends the death message (with the note that the player quit).
                        DebugLog("Death video abandoned: the game is quitting.");
                    }
                    else
                    {
                        DebugLog("All WebP attempts failed; sending text-only death message.");
                        SendDeathMessageOnlyOnce(player, deathMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Death WebP processing failed.", ex);
                SafeDelete(output);
                SendDeathMessageOnlyOnce(player, deathMessage);
            }
            finally
            {
                foreach (string frame in frames)
                    SafeDelete(frame);

                try
                {
                    if (frames.Count > 0)
                    {
                        string stagingDirectory = Path.GetDirectoryName(frames[0]);
                        if (!string.IsNullOrWhiteSpace(stagingDirectory) &&
                            Directory.Exists(stagingDirectory) &&
                            !string.IsNullOrWhiteSpace(_encodingRootDirectory) &&
                            stagingDirectory.IndexOf(Path.Combine(_encodingRootDirectory, "DeathEncoding"), StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            Directory.Delete(stagingDirectory, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Error("Could not remove temporary death encoding directory.", ex);
                }

                _deathProcessing = false;
            }
        }

        /// <summary>
        /// Sends the text-only death message unless it was already delivered, or the game is quitting (in which case
        /// the quit handler sends it, with the quit note, so it is never posted twice).
        /// </summary>
        private void SendDeathMessageOnlyOnce(string player, string deathMessage)
        {
            if (QuitRequested)
                return;

            if (Interlocked.CompareExchange(ref _deathDelivery, 1, 0) != 0)
                return;

            SendDeathMessageOnly(player, deathMessage);
        }

        private void SendDeathMessageOnly(string player, string deathMessage)
        {
            try
            {
                string content = deathMessage;
                SendWebhookJson(
                    ClientConfig.DeathVideoWebhookUrl.Value.Trim(),
                    ClientTextUtils.SanitizeUsername(player),
                    content);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not send death message.", ex);
            }
        }
    }
}
