using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

internal static class WebPTool
{
    private static readonly object Sync = new();
    private static string _toolPath;
    private static string _toolDirectory;
    private static string _initializationError;
    private static bool _initialized;


    /// <summary>Never fewer frames than this per parallel chunk: every chunk costs one extra key frame.</summary>
    private const int MinFramesPerChunk = 16;


    /// <summary>Kills every running encoder (the game is quitting); see <see cref="WebPProcessRunner"/>.</summary>
    internal static void CancelActive()
    {
        WebPProcessRunner.CancelActive();
    }

    internal static bool IsAvailable()
    {
        EnsureTool();
        return !string.IsNullOrWhiteSpace(_toolPath) && File.Exists(_toolPath);
    }

    internal static string GetStatus()
    {
        EnsureTool();
        return "toolPath=" + (_toolPath ?? "<null>") +
               ", toolDirectory=" + (_toolDirectory ?? "<null>") +
               ", initialized=" + _initialized +
               ", error=" + (_initializationError ?? "<none>");
    }

    internal static bool EncodeAnimation(
        List<string> frames,
        string output,
        int fps,
        int quality,
        int method)
    {
        if (WebPProcessRunner.Cancelled)
            return false;

        if (!IsAvailable())
        {
            RelayDiagnostics.Error("Cannot encode WebP because the encoder is unavailable. " + GetStatus());
            return false;
        }

        if (frames == null || frames.Count == 0)
        {
            RelayDiagnostics.Error("Cannot encode WebP because no frames were supplied.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            RelayDiagnostics.Error("Cannot encode WebP because the output path is empty.");
            return false;
        }

        try
        {
            WebPFrameFiles.ValidateFrames(frames);

            string outputDirectory = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDirectory);

            try
            {
                if (File.Exists(output))
                    File.Delete(output);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not remove existing WebP output '" + output + "'.", ex);
                return false;
            }

            int safeFps = Mathf.Clamp(fps, 20, 30);
            int safeQuality = Mathf.Clamp(quality, 0, 100);
            int safeMethod = Mathf.Clamp(method, 0, 6);
            int duration = Mathf.Max(1, Mathf.RoundToInt(1000f / safeFps));

            string frameDirectory = Path.GetDirectoryName(frames[0]);
            if (string.IsNullOrWhiteSpace(frameDirectory))
                frameDirectory = Directory.GetCurrentDirectory();

            foreach (string frame in frames)
            {
                string directory = Path.GetDirectoryName(frame);
                if (!string.Equals(directory, frameDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    RelayDiagnostics.Error(
                        "Cannot encode WebP because staged frames are not in one directory. Frame='" + frame + "'.");
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(Path.GetFileName(output)))
            {
                RelayDiagnostics.Error("Cannot encode WebP because the output filename is empty.");
                return false;
            }

            // One encoder process per chunk of consecutive frames, never more than WebPProcessRunner.MaxEncoderThreads at once
            // (that budget is shared by every encode in the game, see EncoderSlots), and never so many that a
            // chunk gets too short to be worth its own key frame.
            int chunkCount = Math.Max(1, Math.Min(WebPProcessRunner.MaxEncoderThreads, frames.Count / MinFramesPerChunk));

            RelayDiagnostics.Info(
                "WebP encode starting. Executable='" + _toolPath +
                "', Frames=" + frames.Count +
                ", FPS=" + safeFps +
                ", DurationMs=" + duration +
                ", Quality=" + safeQuality +
                ", Method=" + safeMethod +
                ", Workers=" + chunkCount +
                " (thread budget " + WebPProcessRunner.MaxEncoderThreads + " of " + Environment.ProcessorCount + " logical processors)" +
                ", Output='" + output + "'.");

            if (chunkCount > 1)
            {
                bool mergeFailed;
                if (EncodeInParallel(frames, output, frameDirectory, chunkCount, new EncodeSettings(safeQuality, safeMethod, duration), out mergeFailed))
                    return VerifyOutput(output);

                if (WebPProcessRunner.Cancelled || !mergeFailed)
                {
                    SafeDelete(output);
                    return false;
                }

                // Every chunk encoded but they could not be joined: redo it as one piece rather than lose the clip.
                RelayDiagnostics.Warning("Joining the parallel WebP chunks failed; re-encoding the clip in a single piece.");
                SafeDelete(output);
            }

            string commandLine = BuildArguments(frames, output, new EncodeSettings(safeQuality, safeMethod, duration));
            RelayDiagnostics.Info("WebP encoder command: " + commandLine);

            if (!WebPProcessRunner.RunEncoder(_toolPath, commandLine, frameDirectory, output, "single"))
            {
                SafeDelete(output);
                return false;
            }

            return VerifyOutput(output);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("WebP encoder exception.", ex);
            SafeDelete(output);
            return false;
        }
    }

    /// <summary>The img2webp quality, effort method and per-frame duration (ms) of one encode.</summary>
    private readonly struct EncodeSettings
    {
        internal EncodeSettings(int quality, int method, int duration) { Quality = quality; Method = method; Duration = duration; }
        internal int Quality { get; }
        internal int Method { get; }
        internal int Duration { get; }
    }

    /// <summary>
    /// Encodes the frames as <paramref name="chunkCount"/> separate animations at the same time (one img2webp
    /// process each) and joins them into <paramref name="output"/>. Each chunk starts with its own key frame, which
    /// costs a little size compared to one continuous encode but makes the whole encode roughly that many times faster.
    /// </summary>
    private static bool EncodeInParallel(
        List<string> frames,
        string output,
        string frameDirectory,
        int chunkCount,
        EncodeSettings settings,
        out bool mergeFailed)
    {
        mergeFailed = false;

        string outputDirectory = Path.GetDirectoryName(output);
        string stem = Path.GetFileNameWithoutExtension(output);

        List<string> parts = new List<string>();
        Thread[] threads = new Thread[chunkCount];
        bool[] results = new bool[chunkCount];

        int perChunk = frames.Count / chunkCount;
        int remainder = frames.Count % chunkCount;
        int start = 0;

        try
        {
            for (int i = 0; i < chunkCount; i++)
            {
                int count = perChunk + (i < remainder ? 1 : 0);
                List<string> slice = frames.GetRange(start, count);
                start += count;

                string part = Path.Combine(outputDirectory, stem + ".part" + i.ToString(CultureInfo.InvariantCulture) + ".webp");
                SafeDelete(part);
                parts.Add(part);

                string args = BuildArguments(slice, part, settings);
                string label = "chunk " + (i + 1) + "/" + chunkCount + " (" + count + " frames)";
                int index = i;

                threads[i] = new Thread(delegate()
                {
                    try
                    {
                        results[index] = WebPProcessRunner.RunEncoder(_toolPath, args, frameDirectory, part, label);
                    }
                    catch (Exception ex)
                    {
                        RelayDiagnostics.Error("WebP encoder worker " + label + " failed.", ex);
                        results[index] = false;
                    }
                });
                threads[i].IsBackground = true;
                threads[i].Name = "ValheimDiscordRelay.WebP." + i.ToString(CultureInfo.InvariantCulture);
                threads[i].Start();
            }

            // Every worker is bounded by the per-process timeout, and returns at once when the game is quitting.
            for (int i = 0; i < chunkCount; i++)
                threads[i].Join();

            if (WebPProcessRunner.Cancelled)
                return false;

            for (int i = 0; i < chunkCount; i++)
            {
                if (!results[i])
                {
                    RelayDiagnostics.Error("WebP chunk " + (i + 1) + "/" + chunkCount + " failed; the clip was not encoded.");
                    return false;
                }
            }

            string mergeError;
            if (!WebPAnimationMerger.TryMerge(parts, output, out mergeError))
            {
                RelayDiagnostics.Error("Could not join the WebP chunks: " + mergeError);
                mergeFailed = true;
                return false;
            }

            return true;
        }
        finally
        {
            foreach (string part in parts)
                SafeDelete(part);
        }
    }

    private static string BuildArguments(List<string> frames, string output, EncodeSettings settings)
    {
        int quality = settings.Quality, method = settings.Method, duration = settings.Duration;
        StringBuilder args = new StringBuilder();
        args.Append("-loop 1 -min_size ");

        foreach (string frame in frames)
        {
            args.Append("-lossy -q ")
                .Append(quality.ToString(CultureInfo.InvariantCulture))
                .Append(" -m ")
                .Append(method.ToString(CultureInfo.InvariantCulture))
                .Append(" -d ")
                .Append(duration.ToString(CultureInfo.InvariantCulture))
                .Append(" ")
                .Append(WebPFrameFiles.QuoteArgument(Path.GetFileName(frame)))
                .Append(" ");
        }

        args.Append("-o ").Append(WebPFrameFiles.QuoteArgument(output));
        return args.ToString();
    }

    private static bool VerifyOutput(string output)
    {
        if (!File.Exists(output))
        {
            RelayDiagnostics.Error(
                "img2webp returned success but did not create the output file: '" + output + "'.");
            return false;
        }

        FileInfo outputInfo = new FileInfo(output);
        if (outputInfo.Length <= 0)
        {
            RelayDiagnostics.Error("img2webp created an empty output file: '" + output + "'.");
            SafeDelete(output);
            return false;
        }

        RelayDiagnostics.Info(
            "WebP encode completed successfully. OutputBytes=" + outputInfo.Length +
            ", Output='" + output + "'.");

        return true;
    }



    internal static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not delete WebP file '" + path + "'.", ex);
        }
    }

    private static void EnsureTool()
    {
        lock (Sync)
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                string root = Path.Combine(
                    BepInEx.Paths.CachePath,
                    "ValheimDiscordRelay",
                    "WebPTool");

                Directory.CreateDirectory(root);
                _toolDirectory = root;

                var assembly = typeof(WebPTool).Assembly;
                string prefix = "ValheimDiscordRelay.EmbeddedWebP.";
                string[] resources = assembly.GetManifestResourceNames();

                RelayDiagnostics.Info(
                    "WebP embedded resources found: " +
                    string.Join(", ", resources));

                foreach (string resourceName in resources)
                {
                    if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    string fileName = resourceName.Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(fileName))
                        continue;

                    string destination = Path.Combine(root, fileName);

                    using (Stream input = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (input == null)
                        {
                            RelayDiagnostics.Error("Could not open embedded WebP resource: " + resourceName);
                            continue;
                        }

                        bool write = true;
                        if (File.Exists(destination))
                        {
                            FileInfo existing = new FileInfo(destination);
                            if (existing.Length == input.Length)
                                write = false;
                        }

                        if (write)
                        {
                            using (FileStream output = File.Create(destination))
                                input.CopyTo(output);
                        }
                    }
                }

                string path = Path.Combine(root, "img2webp.exe");
                if (!File.Exists(path))
                {
                    _initializationError =
                        "Embedded img2webp.exe was not found after extraction. Expected: " + path;
                    RelayDiagnostics.Error(_initializationError);
                    return;
                }

                _toolPath = path;
                RelayDiagnostics.Info("WebP encoder extracted to: " + _toolPath);

                ProcessStartInfo probeInfo = new ProcessStartInfo
                {
                    FileName = _toolPath,
                    Arguments = "-version",
                    WorkingDirectory = _toolDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process probe = new Process())
                {
                    probe.StartInfo = probeInfo;
                    if (!probe.Start())
                    {
                        _initializationError = "img2webp process could not be started during startup probe.";
                        RelayDiagnostics.Error(_initializationError);
                        return;
                    }

                    string stdout = probe.StandardOutput.ReadToEnd();
                    string stderr = probe.StandardError.ReadToEnd();
                    probe.WaitForExit(10000);

                    RelayDiagnostics.Info(
                        "WebP encoder startup probe completed. ExitCode=" + probe.ExitCode +
                        "\nSTDOUT:\n" + (string.IsNullOrWhiteSpace(stdout) ? "[no output]" : stdout.TrimEnd()) +
                        "\nSTDERR:\n" + (string.IsNullOrWhiteSpace(stderr) ? "[no output]" : stderr.TrimEnd()));
                }
            }
            catch (Exception ex)
            {
                _initializationError = ex.ToString();
                RelayDiagnostics.Error("Could not initialize WebP encoder.", ex);
            }
        }
    }
}
