using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    internal static class WebPTool
    {
        private static readonly object Sync = new();
        private static string _toolPath;
        private static string _toolDirectory;
        private static string _initializationError;
        private static bool _initialized;
        private const int EncoderTimeoutMilliseconds = 120000;

        /// <summary>Never fewer frames than this per parallel chunk: every chunk costs one extra key frame.</summary>
        private const int MinFramesPerChunk = 16;

        /// <summary>
        /// At most a quarter of the machine's logical processors (at least one) may run encoder threads at the same
        /// time: enough to be much faster on a big CPU, while a small one keeps its other threads for the game.
        /// </summary>
        private static readonly int MaxEncoderThreads = Math.Max(1, Environment.ProcessorCount / 4);

        /// <summary>The shared encoder thread budget; every img2webp process holds one slot while it runs.</summary>
        private static readonly SemaphoreSlim EncoderSlots = new SemaphoreSlim(MaxEncoderThreads, MaxEncoderThreads);

        /// <summary>Set when the game is quitting: running encodes are killed and new ones refuse to start.</summary>
        private static volatile bool _cancelled;
        private static readonly object ActiveSync = new();
        private static readonly List<Process> ActiveProcesses = new();

        /// <summary>
        /// Kills every running img2webp process and makes any encode that has not started yet return false at once.
        /// Called when the game is quitting; a worker thread blocked in WaitForExit/ReadToEnd on the encoder would
        /// otherwise keep the game from shutting down until the encoder (or its 2 minute timeout) finished.
        /// </summary>
        internal static void CancelActive()
        {
            _cancelled = true;

            lock (ActiveSync)
            {
                foreach (Process active in ActiveProcesses)
                {
                    try
                    {
                        if (!active.HasExited)
                            active.Kill();
                    }
                    catch (Exception ex)
                    {
                        RelayDiagnostics.Debug("Could not terminate img2webp during quit: " + ex.Message);
                    }
                }
            }
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
            if (_cancelled)
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
                ValidateFrames(frames);

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

                // One encoder process per chunk of consecutive frames, never more than MaxEncoderThreads at once
                // (that budget is shared by every encode in the game, see EncoderSlots), and never so many that a
                // chunk gets too short to be worth its own key frame.
                int chunkCount = Math.Max(1, Math.Min(MaxEncoderThreads, frames.Count / MinFramesPerChunk));

                RelayDiagnostics.Info(
                    "WebP encode starting. Executable='" + _toolPath +
                    "', Frames=" + frames.Count +
                    ", FPS=" + safeFps +
                    ", DurationMs=" + duration +
                    ", Quality=" + safeQuality +
                    ", Method=" + safeMethod +
                    ", Workers=" + chunkCount +
                    " (thread budget " + MaxEncoderThreads + " of " + Environment.ProcessorCount + " logical processors)" +
                    ", Output='" + output + "'.");

                if (chunkCount > 1)
                {
                    bool mergeFailed;
                    if (EncodeInParallel(frames, output, frameDirectory, chunkCount, safeQuality, safeMethod, duration, out mergeFailed))
                        return VerifyOutput(output);

                    if (_cancelled || !mergeFailed)
                    {
                        SafeDelete(output);
                        return false;
                    }

                    // Every chunk encoded but they could not be joined: redo it as one piece rather than lose the clip.
                    RelayDiagnostics.Warning("Joining the parallel WebP chunks failed; re-encoding the clip in a single piece.");
                    SafeDelete(output);
                }

                string commandLine = BuildArguments(frames, output, safeQuality, safeMethod, duration);
                RelayDiagnostics.Info("WebP encoder command: " + commandLine);

                if (!RunEncoder(commandLine, frameDirectory, output, "single"))
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
            int quality,
            int method,
            int duration,
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

                    string args = BuildArguments(slice, part, quality, method, duration);
                    string label = "chunk " + (i + 1) + "/" + chunkCount + " (" + count + " frames)";
                    int index = i;

                    threads[i] = new Thread(delegate()
                    {
                        try
                        {
                            results[index] = RunEncoder(args, frameDirectory, part, label);
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

                if (_cancelled)
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

        private static string BuildArguments(List<string> frames, string output, int quality, int method, int duration)
        {
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
                    .Append(QuoteArgument(Path.GetFileName(frame)))
                    .Append(" ");
            }

            args.Append("-o ").Append(QuoteArgument(output));
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

        /// <summary>
        /// Runs one img2webp process, but only once one of the shared encoder slots is free, so the whole game never
        /// runs more than <see cref="MaxEncoderThreads"/> encoder threads at the same time (a death clip and a boss clip
        /// finishing together share the budget instead of doubling it).
        /// </summary>
        private static bool RunEncoder(string commandLine, string workingDirectory, string output, string label)
        {
            while (!EncoderSlots.Wait(250))
            {
                if (_cancelled)
                    return false;
            }

            try
            {
                if (_cancelled)
                    return false;

                return RunEncoderProcess(commandLine, workingDirectory, output, label);
            }
            finally
            {
                EncoderSlots.Release();
            }
        }

        private static bool RunEncoderProcess(string commandLine, string workingDirectory, string output, string label)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _toolPath,
                Arguments = commandLine,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;

                try
                {
                    if (!process.Start())
                    {
                        RelayDiagnostics.Error("img2webp process could not be started (" + label + ").");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Error("Could not start img2webp (" + label + "). Executable='" + _toolPath + "'.", ex);
                    return false;
                }

                // The game keeps running while a clip encodes (the player respawns and plays on), so the encoder must
                // not take CPU time away from it.
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) { RelayDiagnostics.Debug("Could not lower the img2webp priority: " + ex.Message); }

                // Registered so a quit can kill it. Checked under the same lock CancelActive uses, so a quit that
                // lands between Start() and here still kills this process.
                lock (ActiveSync)
                {
                    ActiveProcesses.Add(process);

                    if (_cancelled)
                    {
                        try { process.Kill(); }
                        catch { }
                    }
                }

                try
                {
                    string stdout = string.Empty;
                    string stderr = string.Empty;
                    Exception stdoutException = null;
                    Exception stderrException = null;

                    Thread stdoutThread = new Thread(delegate()
                    {
                        try { stdout = process.StandardOutput.ReadToEnd(); }
                        catch (Exception ex) { stdoutException = ex; }
                    });

                    Thread stderrThread = new Thread(delegate()
                    {
                        try { stderr = process.StandardError.ReadToEnd(); }
                        catch (Exception ex) { stderrException = ex; }
                    });

                    stdoutThread.IsBackground = true;
                    stderrThread.IsBackground = true;
                    stdoutThread.Start();
                    stderrThread.Start();

                    bool exited = process.WaitForExit(EncoderTimeoutMilliseconds);

                    if (_cancelled)
                    {
                        // The game is quitting and CancelActive killed the encoder. Do not wait for the reader
                        // threads (they are background threads and end on their own).
                        RelayDiagnostics.Info("WebP encode (" + label + ") cancelled because the game is quitting.");
                        try { if (!process.HasExited) process.Kill(); }
                        catch { }
                        SafeDelete(output);
                        return false;
                    }

                    if (!exited)
                    {
                        RelayDiagnostics.Error(
                            "img2webp (" + label + ") timed out after " + EncoderTimeoutMilliseconds +
                            " ms. Executable='" + _toolPath +
                            "', Output='" + output + "'.");

                        try { process.Kill(); }
                        catch (Exception ex) { RelayDiagnostics.Error("Could not terminate timed-out img2webp process.", ex); }

                        stdoutThread.Join(5000);
                        stderrThread.Join(5000);
                        return false;
                    }

                    stdoutThread.Join(5000);
                    stderrThread.Join(5000);

                    int exitCode = process.ExitCode;

                    if (stdoutException != null)
                        RelayDiagnostics.Error("Could not read img2webp stdout.", stdoutException);
                    if (stderrException != null)
                        RelayDiagnostics.Error("Could not read img2webp stderr.", stderrException);

                    if (!string.IsNullOrWhiteSpace(stdout))
                        RelayDiagnostics.Info("img2webp stdout (" + label + "):\n" + stdout.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(stderr))
                        RelayDiagnostics.Info("img2webp stderr (" + label + "):\n" + stderr.TrimEnd());

                    if (exitCode != 0)
                    {
                        RelayDiagnostics.Error(
                            "img2webp failed (" + label + "). ExitCode=" + exitCode +
                            ", Output='" + output + "'.");
                        return false;
                    }

                    if (!File.Exists(output) || new FileInfo(output).Length <= 0)
                    {
                        RelayDiagnostics.Error(
                            "img2webp (" + label + ") returned exit code 0 but did not produce a usable output file: '" + output + "'.");
                        SafeDelete(output);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    lock (ActiveSync)
                    {
                        ActiveProcesses.Remove(process);
                    }
                }
            }
        }

        private static void ValidateFrames(List<string> frames)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                string frame = frames[i];

                if (string.IsNullOrWhiteSpace(frame))
                    throw new InvalidDataException("Frame " + i + " has an empty path.");

                if (!File.Exists(frame))
                    throw new FileNotFoundException("Frame " + i + " does not exist.", frame);

                FileInfo info = new FileInfo(frame);
                if (info.Length <= 0)
                    throw new InvalidDataException("Frame " + i + " is empty: " + frame);

                if (!IsSupportedImageFile(frame))
                    throw new InvalidDataException("Frame " + i + " has an unsupported image extension: " + frame);

                if (!HasValidImageSignature(frame))
                    throw new InvalidDataException("Frame " + i + " does not have a valid JPEG/PNG signature: " + frame);
            }
        }

        private static bool IsSupportedImageFile(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasValidImageSignature(string path)
        {
            byte[] header = new byte[8];
            using (FileStream stream = File.OpenRead(path))
            {
                int total = 0;
                while (total < header.Length)
                {
                    int read = stream.Read(header, total, header.Length - total);
                    if (read <= 0)
                        return false;
                    total += read;
                }
            }

            bool png = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E &&
                       header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A &&
                       header[6] == 0x1A && header[7] == 0x0A;

            bool jpg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            return png || jpg;
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
                return "\"\"";

            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(c);
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static void SafeDelete(string path)
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
}
