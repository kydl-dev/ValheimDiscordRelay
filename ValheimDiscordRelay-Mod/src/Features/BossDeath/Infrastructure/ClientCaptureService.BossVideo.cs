using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Boss death video.
///
/// Normally (shared mode, while [Player Deaths] Client - Video Enabled is on) it does NOT capture anything itself: the
/// player-death recorder already keeps the last few seconds of frames in memory, so the boss clip is cut from
/// those same frames. One capture + JPEG encode per frame, however many clips are wanted. In this mode the clip
/// uses the death video's FPS and resolution; only the boss Pre/Post durations are its own.
///
/// If the death video is switched off, it falls back to its own rolling buffer with its own settings
/// ([Boss Death] Video *), which only runs while this client is fighting a boss it owns (and for the post-death
/// window), so it costs nothing the rest of the time.
///
/// With [Boss Death] Video Source = Last Attacker the boss owner asks the player who landed the last hit to record
/// instead (BossVideoRpc). That player's game cuts the clip from its own death-recorder frames, so its FPS and
/// resolution apply, and tells the owner when it is ready. The owner's own clip is only used if that fails.
/// </summary>
internal sealed partial class ClientCaptureService
{
    /// <summary>Keep buffering this long after the last boss hit this client handled.</summary>
    private const float BossEngagedSeconds = 45f;

    private readonly object _bossSync = new();
    private readonly List<FrameEntry> _bossFrames = new();

    private ScreenCapturePipeline _bossPipeline;
    private Texture2D _bossEncodeTexture;
    private int _bossEncodeWidth;
    private int _bossEncodeHeight;

    private float _bossEngagedUntil = -1f;
    private float _nextBossFrameTime;
    private float _bossDeathTime;
    private string _bossVideoAvatarUrl;
    private bool _warnedBossWebhookEmpty;

    private volatile bool _bossFrameCaptureInProgress;
    private volatile bool _bossRecording;
    private volatile bool _bossProcessing;

    /// <summary>Boss identity of the clip being recorded; lets the tracker release the held-back announcement.</summary>
    private string _bossAnnounceKey;

    /// <summary>True when the current boss clip is being cut from the death recorder's frames (decided when it starts).</summary>
    private volatile bool _bossUsesSharedFrames;

    /// <summary>
    /// Peer id of the boss owner when this clip is recorded on its request (Video Source = Last Attacker);
    /// 0 when this client owns the boss itself.
    /// </summary>
    private long _bossRemoteOwner;

    /// <summary>
    /// How long past the end of its own clip the boss owner waits for the last attacker's answer before it uses
    /// its own video. The answer normally arrives within a fraction of a second of the death.
    /// </summary>
    private const float RemoteReplyGraceSeconds = 1.5f;

    /// <summary>
    /// Shared mode: the boss clip reuses the frames the player-death recorder is already capturing.
    /// </summary>
    private static bool UsesSharedDeathFrames()
    {
        return ClientConfig.DeathVideoActive;
    }

    /// <summary>
    /// Called whenever this client applies a hit to a boss it owns; keeps the rolling buffer running.
    /// </summary>
    internal void NoteBossEngaged()
    {
        _bossEngagedUntil = Time.unscaledTime + BossEngagedSeconds;
    }

    /// <summary>
    /// Called every frame from Tick(). Captures boss frames only while engaged/recording, and frees them otherwise.
    /// </summary>
    private void TickBossVideo()
    {
        // Shared mode: the death recorder is the only thing capturing; nothing to do here. (A clip that is
        // already recording keeps the mode it started with.)
        bool shared = _bossRecording ? _bossUsesSharedFrames : UsesSharedDeathFrames();
        if (shared)
        {
            if (!_bossRecording)
            {
                lock (_bossSync)
                {
                    if (_bossFrames.Count > 0)
                        _bossFrames.Clear();
                }
            }

            return;
        }

        bool wanted = BossConfig.On &&
                      BossConfig.ClientOn &&
                      BossConfig.VideoEnabled.Value &&
                      !string.IsNullOrWhiteSpace(BossConfig.WebhookUrl.Value);

        bool active = wanted &&
                      !_bossProcessing &&
                      (_bossRecording || Time.unscaledTime < _bossEngagedUntil);

        if (!active)
        {
            if (!_bossRecording)
            {
                lock (_bossSync)
                {
                    if (_bossFrames.Count > 0)
                        _bossFrames.Clear();
                }
            }

            return;
        }

        if (_bossFrameCaptureInProgress)
            return;

        int fps = Mathf.Clamp(BossConfig.VideoFps.Value, 20, 30);
        if (Time.unscaledTime < _nextBossFrameTime)
            return;

        _nextBossFrameTime = Time.unscaledTime + 1f / fps;
        _host.StartCoroutine(CaptureBossFrame());
    }

    private IEnumerator CaptureBossFrame()
    {
        _bossFrameCaptureInProgress = true;
        yield return new WaitForEndOfFrame();

        if (_bossPipeline == null)
            _bossPipeline = new ScreenCapturePipeline();

        int width, height;
        BossConfig.GetVideoResolution(out width, out height);

        float capturedAt = Time.unscaledTime;

        _bossPipeline.CaptureAsync(
            width,
            height,
            (rgb24, w, h) => OnBossFrameCaptured(rgb24, w, h, capturedAt),
            () => { _bossFrameCaptureInProgress = false; });
    }

    private void OnBossFrameCaptured(byte[] rgb24, int width, int height, float capturedAt)
    {
        try
        {
            EnsureBossEncodeTexture(width, height);
            _bossEncodeTexture.LoadRawTextureData(rgb24);
            _bossEncodeTexture.Apply(false, false);

            byte[] jpg = ImageConversion.EncodeToJPG(_bossEncodeTexture, 85);
            if (jpg == null || jpg.Length == 0)
                return;

            lock (_bossSync)
            {
                _bossFrames.Add(new FrameEntry
                {
                    Data = jpg,
                    Time = capturedAt
                });

                if (!_bossRecording)
                {
                    float cutoff = capturedAt - (Mathf.Clamp(BossConfig.VideoPreDuration.Value, 2, 4) + 0.1f);
                    for (int i = _bossFrames.Count - 1; i >= 0; i--)
                    {
                        if (_bossFrames[i].Time < cutoff)
                            _bossFrames.RemoveAt(i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Boss frame capture failed.", ex);
        }
        finally
        {
            _bossFrameCaptureInProgress = false;
        }
    }

    private void EnsureBossEncodeTexture(int width, int height)
    {
        if (_bossEncodeTexture != null && _bossEncodeWidth == width && _bossEncodeHeight == height)
            return;

        if (_bossEncodeTexture != null)
            UnityEngine.Object.Destroy(_bossEncodeTexture);

        _bossEncodeTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
        _bossEncodeWidth = width;
        _bossEncodeHeight = height;
    }

    /// <summary>
    /// Starts the post-death part of the boss video. Called once, on the client that owned the boss when it died.
    /// </summary>
    /// <param name="bossName">Boss display name, for logging.</param>
    /// <param name="avatarUrl">Webhook avatar to use for the upload, or null.</param>
    /// <param name="announceKey">Boss identity; the death announcement is held until this video is ready.</param>
    /// <param name="remoteOwner">
    /// 0 when this client owns the boss. Otherwise the peer id of the boss owner that asked this client, as the last
    /// attacker, to record the video; "ready" is then reported back to that owner instead of released locally.
    /// </param>
    /// <returns>True if a video will be made (the caller may then hold the announcement for it).</returns>
    internal bool BeginBossVideo(string bossName, string avatarUrl, string announceKey, long remoteOwner = 0)
    {
        if (!BossConfig.ClientOn || !BossConfig.On || !BossConfig.VideoEnabled.Value)
            return false;

        if (ZNet.instance != null && ZNet.instance.IsDedicated())
            return false;

        if (string.IsNullOrWhiteSpace(BossConfig.WebhookUrl.Value))
        {
            if (remoteOwner == 0 && !_warnedBossWebhookEmpty)
            {
                _warnedBossWebhookEmpty = true;
                RelayDiagnostics.Warning(
                    "A boss died in your game, but [Boss Death] Server - Webhook URL is empty on this client; no boss video will be uploaded.");
            }
            return false;
        }

        if (remoteOwner != 0)
        {
            // Recorded on request as the last attacker: only possible from the death recorder's frames, because
            // this client is not the boss owner and so never ran the boss recorder that needs "engaged" hits.
            bool haveFrames;
            lock (_sync)
            {
                haveFrames = _frames.Count > 0;
            }

            if (!UsesSharedDeathFrames() || !haveFrames)
            {
                DebugLog("Declined the last-attacker boss video for '" + bossName +
                         "': [Player Deaths] Client - Video Enabled is off or no frames are buffered.");
                return false;
            }
        }

        if (_bossRecording || _bossProcessing)
        {
            DebugLog("Boss video already in progress; skipping video for '" + bossName + "'.");
            return false;
        }

        lock (_bossSync)
        {
            _bossUsesSharedFrames = UsesSharedDeathFrames();
            _bossRecording = true;
            _bossDeathTime = Time.unscaledTime;
            _bossVideoAvatarUrl = avatarUrl;
            _bossAnnounceKey = announceKey;
            _bossRemoteOwner = remoteOwner;
        }

        if (_bossUsesSharedFrames)
        {
            DebugLog("Boss death video started for '" + bossName + "' from the shared death-video frames. Settings: FPS=" +
                     ClientConfig.DeathVideoFps.Value + ", Resolution=" + ClientConfig.DeathVideoResolution.Value +
                     " (Death Video), Pre=" + BossConfig.VideoPreDuration.Value +
                     ", Post=" + BossConfig.VideoPostDuration.Value);
        }
        else
        {
            DebugLog("Boss death video started for '" + bossName + "'. Settings: FPS=" + BossConfig.VideoFps.Value +
                     ", Resolution=" + BossConfig.VideoResolution.Value +
                     ", Pre=" + BossConfig.VideoPreDuration.Value +
                     ", Post=" + BossConfig.VideoPostDuration.Value);
        }

        _host.StartCoroutine(FinishBossRecording(bossName));
        return true;
    }

    private IEnumerator FinishBossRecording(string bossName)
    {
        float postEnd = _bossDeathTime + Mathf.Clamp(BossConfig.VideoPostDuration.Value, 2, 4);

        while (Time.unscaledTime < postEnd)
            yield return null;

        string avatarUrl;
        string announceKey;
        long remoteOwner;

        lock (_bossSync)
        {
            avatarUrl = _bossVideoAvatarUrl;
            announceKey = _bossAnnounceKey;
            remoteOwner = _bossRemoteOwner;
        }

        // Video Source = Last Attacker: this client owns the boss and asked the last attacker's game to record the
        // video. Give the answer a moment to arrive, then either leave the job to that game or use this clip.
        // (Recording stays "on" meanwhile so the frames this clip needs are not pruned.)
        if (remoteOwner == 0 && !string.IsNullOrEmpty(announceKey))
        {
            float graceEnd = postEnd + RemoteReplyGraceSeconds;
            while (BossKillTracker.IsRemoteReplyPending(announceKey) && Time.unscaledTime < graceEnd)
                yield return null;

            if (BossKillTracker.IsVideoTakenOver(announceKey))
            {
                _bossRecording = false;

                lock (_bossSync)
                {
                    _bossFrames.Clear();
                }

                DebugLog("Boss video skipped: the last attacker's game is recording it.");
                yield break;
            }
        }

        _bossRecording = false;
        _bossProcessing = true;

        List<FrameEntry> selected = new List<FrameEntry>();
        bool shared = _bossUsesSharedFrames;
        float start = _bossDeathTime - Mathf.Clamp(BossConfig.VideoPreDuration.Value, 2, 4);

        if (shared)
        {
            // Copy (do not remove) the window from the death recorder's buffer. A FrameEntry's JPEG bytes are never
            // modified after capture, so the boss clip just references the same arrays: the "duplicate" costs no
            // extra memory. Leaving the originals in place also keeps a player death in the same moment intact;
            // the normal rolling prune drops them afterwards.
            lock (_sync)
            {
                for (int i = 0; i < _frames.Count; i++)
                {
                    FrameEntry frame = _frames[i];
                    if (frame.Time >= start && frame.Time <= postEnd)
                        selected.Add(frame);
                }
            }

            selected.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
        else
        {
            lock (_bossSync)
            {
                for (int i = _bossFrames.Count - 1; i >= 0; i--)
                {
                    if (_bossFrames[i].Time >= start && _bossFrames[i].Time <= postEnd)
                    {
                        selected.Add(_bossFrames[i]);
                        _bossFrames.RemoveAt(i);
                    }
                }

                selected.Reverse();
                _bossFrames.Clear();
            }
        }

        DebugLog("Boss capture complete. Selected " + selected.Count + " frames" +
                 (shared ? " (shared with the death video)." : "."));

        // Everything the worker thread needs is read here, on the main thread.
        string webhookUrl = BossConfig.WebhookUrl.Value.Trim();
        string username = ClientTextUtils.SanitizeUsername(BossConfig.WebhookUsername.Value);

        // The clip must play back at the rate its frames were captured at.
        int fps = shared
            ? Mathf.Clamp(ClientConfig.DeathVideoFps.Value, 20, 30)
            : Mathf.Clamp(BossConfig.VideoFps.Value, 20, 30);

        ThreadPool.QueueUserWorkItem(delegate
        {
            // An exception that escapes a pool thread ends the whole game process: catch everything here.
            try
            {
                List<string> staged = StageDeathFrames(selected, "boss-");
                DebugLog("Boss frame staging complete. Staged " + staged.Count + " of " + selected.Count + " frames.");
                ProcessBossVideo(new BossVideoJob
                {
                    BossName = bossName, WebhookUrl = webhookUrl, Username = username, AvatarUrl = avatarUrl, Fps = fps,
                    Frames = staged, AnnounceKey = announceKey, RemoteOwner = remoteOwner
                });
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Boss video processing failed.", ex);
                ReleaseBossAnnouncement(announceKey, remoteOwner);
                _bossProcessing = false;
            }
        });
    }

    private void DisposeBossVideo()
    {
        _bossRecording = false;
        _bossProcessing = false;

        lock (_bossSync)
        {
            _bossFrames.Clear();
        }

        if (_bossPipeline != null)
        {
            _bossPipeline.Dispose();
            _bossPipeline = null;
        }

        if (_bossEncodeTexture != null)
        {
            UnityEngine.Object.Destroy(_bossEncodeTexture);
            _bossEncodeTexture = null;
        }
    }
}
