using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ValheimDiscordRelay.Client;

internal static class WebPProcessRunner
{
    /// <summary>
    /// Runs one img2webp process, but only once one of the shared encoder slots is free, so the whole game never
    /// runs more than <see cref="MaxEncoderThreads"/> encoder threads at the same time (a death clip and a boss clip
    /// finishing together share the budget instead of doubling it).
    /// </summary>
    internal static bool RunEncoder(string toolPath, string commandLine, string workingDirectory, string output, string label)
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

            return RunEncoderProcess(toolPath, commandLine, workingDirectory, output, label);
        }
        finally
        {
            EncoderSlots.Release();
        }
    }

    private static bool RunEncoderProcess(string toolPath, string commandLine, string workingDirectory, string output, string label)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = toolPath,
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
                RelayDiagnostics.Error("Could not start img2webp (" + label + "). Executable='" + toolPath + "'.", ex);
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
                    WebPTool.SafeDelete(output);
                    return false;
                }

                if (!exited)
                {
                    RelayDiagnostics.Error(
                        "img2webp (" + label + ") timed out after " + EncoderTimeoutMilliseconds +
                        " ms. Executable='" + toolPath +
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
                    WebPTool.SafeDelete(output);
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

    /// <summary>
    /// At most a quarter of the machine's logical processors (at least one) may run encoder threads at the same
    /// time: enough to be much faster on a big CPU, while a small one keeps its other threads for the game.
    /// </summary>
    private const int EncoderTimeoutMilliseconds = 120000;

    internal static readonly int MaxEncoderThreads = Math.Max(1, Environment.ProcessorCount / 4);

    /// <summary>The shared encoder thread budget; every img2webp process holds one slot while it runs.</summary>
    private static readonly SemaphoreSlim EncoderSlots = new(MaxEncoderThreads, MaxEncoderThreads);

    /// <summary>Set when the game is quitting: running encodes are killed and new ones refuse to start.</summary>
    private static volatile bool _cancelled;

    /// <summary>True once the game is quitting.</summary>
    internal static bool Cancelled { get { return _cancelled; } }
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
}
