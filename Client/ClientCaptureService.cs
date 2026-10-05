using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    internal sealed partial class ClientCaptureService : IDisposable
    {
        private readonly MonoBehaviour _host;
        private readonly object _sync = new();

        private string _rootDirectory;
        private string _encodingRootDirectory;

        /// <summary>
        /// Legacy per-frame JPEG directory, still swept by CleanupOldFiles for old leftovers.
        /// </summary>
        private string _frameDirectory;
        private bool _screenshotInProgress;

        /// <summary>True from the moment of death until the post-death window has elapsed (frames must not be pruned).</summary>
        private volatile bool _deathRecording;

        /// <summary>
        /// True while the death clip is being staged/encoded/uploaded on a worker thread. Has no effect on the
        /// rolling frame buffer; it only stops a second death from starting a second video at the same time.
        /// </summary>
        private volatile bool _deathProcessing;
        private volatile bool _frameCaptureInProgress;
        private float _nextFrameTime;
        private float _deathTime;
        private string _deathPlayer;
        private string _deathMessage;

        /// <summary>The death-cause line in its raw Discord form (not code-block wrapped), for building the quit message.</summary>
        private string _deathQuote;

        /// <summary>
        /// 0 until the Discord message for the current death has been handed over (video upload succeeded, or a
        /// text-only message was sent); then 1. Claimed with Interlocked so the worker thread and the quit handler
        /// never both send it.
        /// </summary>
        private int _deathDelivery;

        /// <summary>Latched by OnApplicationQuitting; worker threads poll it to stop encoding/uploading at once.</summary>
        private int _quitState;

        /// <summary>
        /// GPU-side capture pipeline shared by the rolling death-video buffer and manual screenshots.
        /// </summary>
        private ScreenCapturePipeline _capturePipeline;

        /// <summary>
        /// Reused, persistent encode target for rolling death-video frames.
        /// </summary>
        private Texture2D _frameEncodeTexture;
        private int _frameEncodeWidth;
        private int _frameEncodeHeight;
        private Texture2D _screenshotEncodeTexture;
        private int _screenshotEncodeWidth;
        private int _screenshotEncodeHeight;

        /// <summary>
        /// Next Time.unscaledTime at which a background CleanupOldFiles scan may start.
        /// </summary>
        private float _nextCleanupCheckTime;
        private volatile bool _cleanupInProgress;
        private const float CleanupIntervalSeconds = 300f;

        private Player _lastKnownLocalPlayer;
        private string _lastKnownPlayerName = "Valheim Player";
        private HitData _lastLocalPlayerHit;
        private float _lastLocalPlayerHitTime = -99999f;

        /// <summary>Snapshot of the last creature/player that damaged the local player (captured at hit time).</summary>
        private AttackerInfo _lastAttacker;

        /// <summary>Frame in which health first read zero without Player.OnDeath having fired yet; -1 if none.</summary>
        private int _pendingDeathFrame = -1;
        private float _lastDeathEventTime = -99999f;
        private const float RepeatDeathMessageWindowSeconds = 30f;
        private bool _deathEventHandledForCurrentPlayer;
        private bool _haveKnownLocalPlayer;


        internal ClientCaptureService(MonoBehaviour host)
        {
            _host = host;
        }

        internal void Initialize()
        {
            DebugLog("=== ValheimDiscordRelay death-video logger started ===");
            DebugLog("Plugin version: " + ClientPlugin.PluginVersion);

            _rootDirectory = Path.Combine(BepInEx.Paths.CachePath, "ValheimDiscordRelay");

            _encodingRootDirectory = Path.Combine(Path.GetTempPath(), "ValheimDiscordRelay");
            _frameDirectory = Path.Combine(_rootDirectory, "DeathFrames");

            try
            {
                Directory.CreateDirectory(_frameDirectory);
                Directory.CreateDirectory(_encodingRootDirectory);
                CleanupOldFiles();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not initialize capture cache.", ex);
            }

            _capturePipeline = new ScreenCapturePipeline();
        }

        internal void Tick()
        {
            if (!_cleanupInProgress && Time.unscaledTime >= _nextCleanupCheckTime)
            {
                _nextCleanupCheckTime = Time.unscaledTime + CleanupIntervalSeconds;
                _cleanupInProgress = true;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { CleanupOldFiles(); }
                    catch (Exception ex) { RelayDiagnostics.Error("Background cleanup failed.", ex); }
                    finally { _cleanupInProgress = false; }
                });
            }

            // Death detection always tracks the local player (it also feeds the weekly report and the session-teardown
            // guard); whether a death is announced is decided in NotifyDeath by the [Player Deaths] switches.
            CheckLocalPlayerLifecycle();

            KeyboardShortcut shortcut = ClientConfig.ScreenshotKey.Value;
            if (ClientConfig.ScreenshotsActive &&
                !_screenshotInProgress &&
                shortcut.MainKey != KeyCode.None &&
                shortcut.IsDown())
            {
                _host.StartCoroutine(CaptureManualScreenshot());
            }

            TickBossVideo();

            // The rolling buffer is independent of death/boss *processing* (staging, WebP encode, upload). It keeps
            // running while either is in flight, so a boss kill (or second death) that lands during an upload still
            // has fresh frames to cut its clip from. Only the "recording" flags pause pruning, never capture.
            if (!ClientConfig.DeathVideoActive ||
                _frameCaptureInProgress)
                return;

            int fps = Mathf.Clamp(ClientConfig.DeathVideoFps.Value, 20, 30);
            float interval = 1f / fps;

            if (Time.unscaledTime >= _nextFrameTime)
            {
                _nextFrameTime = Time.unscaledTime + interval;
                _host.StartCoroutine(CaptureRollingFrame());
            }
        }

        private void CleanupOldFiles()
        {
            if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory))
                return;

            try
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-24);

                if (Directory.Exists(_frameDirectory))
                {
                    foreach (string file in Directory.GetFiles(_frameDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < cutoff)
                                File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            RelayDiagnostics.Error("Could not clean old death frame '" + file + "'.", ex);
                        }
                    }
                }

                string encodingRoot = Path.Combine(_encodingRootDirectory, "DeathEncoding");
                if (Directory.Exists(encodingRoot))
                {
                    foreach (string directory in Directory.GetDirectories(encodingRoot))
                    {
                        try
                        {
                            if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                                Directory.Delete(directory, true);
                        }
                        catch (Exception ex)
                        {
                            RelayDiagnostics.Error("Could not clean old death encoding directory '" + directory + "'.", ex);
                        }
                    }
                }

                foreach (string file in Directory.GetFiles(_rootDirectory, "death-*.webp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        RelayDiagnostics.Error("Could not clean old WebP output '" + file + "'.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Death cache cleanup failed.", ex);
            }
        }

        private static void SafeDelete(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return;

            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not delete file '" + file + "'.", ex);
            }
        }

        /// <summary>
        /// Logs a death-video trace line at DEBUG severity via RelayDiagnostics.
        /// </summary>
        /// <param name="message">The message to log.</param>
        private void DebugLog(string message)
        {
            RelayDiagnostics.Debug("[DeathVideo] " + message);
        }

        public void Dispose()
        {
            DebugLog("Capture service disposing.");
            _deathRecording = false;
            _deathProcessing = false;

            lock (_sync)
            {
                _frames.Clear();
            }

            if (_capturePipeline != null)
            {
                _capturePipeline.Dispose();
                _capturePipeline = null;
            }

            if (_frameEncodeTexture != null)
            {
                UnityEngine.Object.Destroy(_frameEncodeTexture);
                _frameEncodeTexture = null;
            }

            if (_screenshotEncodeTexture != null)
            {
                UnityEngine.Object.Destroy(_screenshotEncodeTexture);
                _screenshotEncodeTexture = null;
            }

            DisposeBossVideo();

            CleanupOldFiles();
        }
    }
}
