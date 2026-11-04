using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

internal sealed partial class ClientCaptureService
{
    private void ProcessBossVideo(BossVideoJob job)
    {
        string bossName = job.BossName, webhookUrl = job.WebhookUrl, username = job.Username, avatarUrl = job.AvatarUrl;
        string announceKey = job.AnnounceKey;
        int fps = job.Fps;
        long remoteOwner = job.RemoteOwner;
        List<string> frames = job.Frames;

        if (frames == null || frames.Count == 0)
        {
            RelayDiagnostics.Warning("No frames were captured for the '" + bossName + "' death video; nothing was uploaded.");
            ReleaseBossAnnouncement(announceKey, remoteOwner);
            _bossProcessing = false;
            return;
        }

        frames.Sort(StringComparer.OrdinalIgnoreCase);

        string outputDirectory = Path.GetDirectoryName(frames[0]);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            outputDirectory = _encodingRootDirectory;

        string output = Path.Combine(outputDirectory, "boss-death-" + DateTime.UtcNow.ToString("HHmmss") + ".webp");

        // Recorded for a boss owner: it may take its own video back (cancel) if this game was too slow to answer.
        bool cancelled = false;

        try
        {
            if (QuitRequested)
            {
                DebugLog("Boss video skipped: the game is quitting.");
                return;
            }

            if (!WebPTool.IsAvailable())
                throw new InvalidOperationException("Embedded WebP encoder is not available. " + WebPTool.GetStatus());

            bool uploaded = false;
            int[] qualities = new[] { 80, 65, 50, 35 };
            int[] methods = new[] { 6, 5, 4, 3 };

            for (int i = 0; i < qualities.Length && !uploaded; i++)
            {
                if (QuitRequested)
                    break;

                if (remoteOwner != 0 && BossKillTracker.IsRemoteCancelled(announceKey))
                {
                    cancelled = true;
                    break;
                }

                DebugLog("Boss WebP attempt " + (i + 1) + "/4: quality=" + qualities[i] + ", method=" + methods[i] + ".");
                SafeDelete(output);

                if (!WebPTool.EncodeAnimation(frames, output, fps, qualities[i], methods[i]))
                {
                    DebugLog("Boss WebP attempt " + (i + 1) + " failed during encoding.");
                    continue;
                }

                if (!File.Exists(output))
                {
                    DebugLog("Boss WebP attempt " + (i + 1) + " reported success but output file was missing.");
                    continue;
                }

                long size = new FileInfo(output).Length;
                DebugLog("Boss WebP attempt " + (i + 1) + " produced " + size + " bytes.");

                if (size > 20L * 1024L * 1024L)
                {
                    DebugLog("Boss WebP attempt " + (i + 1) + " exceeded 20 MiB and was rejected.");
                    continue;
                }

                if (remoteOwner != 0 && BossKillTracker.IsRemoteCancelled(announceKey))
                {
                    cancelled = true;
                    break;
                }

                // The video is encoded and about to upload: let the boss announcement go out now, so both land in
                // Discord together. (Safe to call again on a retry; the tracker ignores repeats.)
                BossKillTracker.ReleaseDeath(announceKey, remoteOwner);

                bool tooLarge;
                byte[] fileBytes = File.ReadAllBytes(output);
                string fileName = Path.GetFileName(output);

                if (UploadWithRetry(
                        () => SendWebhookFile(webhookUrl, username, fileName, "image/webp", fileBytes, null, avatarUrl),
                        out tooLarge))
                {
                    uploaded = true;
                    DebugLog("Boss death WebP uploaded successfully.");
                }
                else if (tooLarge)
                {
                    DebugLog("Boss WebP attempt " + (i + 1) + " was too large for Discord; trying a smaller encode.");
                }
                else
                {
                    DebugLog("Boss death WebP upload failed; not re-encoding (the file was not the problem).");
                    break;
                }
            }

            if (cancelled)
                DebugLog("The boss owner used its own video; this game's boss video was not uploaded.");
            else if (!uploaded && QuitRequested)
                DebugLog("The boss video was abandoned: the game is quitting.");
            else if (!uploaded)
                RelayDiagnostics.Warning("The '" + bossName + "' death video could not be encoded or uploaded.");
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Boss death WebP processing failed.", ex);
        }
        finally
        {
            // If encoding failed before any upload was attempted, the announcement still must not stay held.
            ReleaseBossAnnouncement(announceKey, remoteOwner);

            if (remoteOwner != 0)
                BossKillTracker.ClearRemoteCancelled(announceKey);

            SafeDelete(output);

            foreach (string frame in frames)
                SafeDelete(frame);

            try
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
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not remove temporary boss encoding directory.", ex);
            }

            _bossProcessing = false;
        }
    }

    /// <summary>
    /// Lets the boss announcement go out (boss owner: locally; last attacker: by telling the owner), unless the owner
    /// has already taken its own video instead, in which case it releases the announcement itself.
    /// </summary>
    private static void ReleaseBossAnnouncement(string announceKey, long remoteOwner)
    {
        if (remoteOwner != 0 && BossKillTracker.IsRemoteCancelled(announceKey))
            return;

        BossKillTracker.ReleaseDeath(announceKey, remoteOwner);
    }
}
