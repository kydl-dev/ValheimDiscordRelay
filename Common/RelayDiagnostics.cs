using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace ValheimDiscordRelay
{
    // The single logging entry point for the whole mod (both the Client and
    // Server plugin components). Every call site - main thread or worker
    // thread - funnels through here and through here only: nothing else in
    // the codebase opens ValheimDiscordRelay.log or calls BepInEx's logger
    // directly. All four severities are always written to that one file
    // (severity only decides whether/how the line is also mirrored to
    // BepInEx's own console/LogOutput.log), in the fixed format:
    //
    //   <UTC timestamp> [ValheimDiscordRelay] - LEVEL-<message>
    //
    // where LEVEL is exactly one of INFO, DEBUG, WARNING or ERROR, matching
    // whichever of Info/Debug/Warning/Error was actually called - a method
    // named *Log for one severity never emits a line tagged with another.
    //
    // Every write is queued in-memory and performed by one dedicated
    // background thread, so no caller ever blocks on file I/O or waits on a
    // lock held by another thread doing the same.
    internal static class RelayDiagnostics
    {
        private const string Component = "[ValheimDiscordRelay] - ";

        private struct LogEntry
        {
            public string Line;
            public string Level; // null = do not mirror to BepInEx's logger
            public string Message;
        }

        private static readonly object InitSync = new();
        private static string _path;
        private static ManualLogSource _bepLog;
        private static bool _initialized;
        private static readonly TimeSpan LogLifetime = TimeSpan.FromHours(24);
        private static DateTime _lastRotationCheck = DateTime.MinValue;

        private static readonly BlockingCollection<LogEntry> _queue = new(new ConcurrentQueue<LogEntry>());

        private static Thread _writerThread;

        internal static string LogPath
        {
            get
            {
                EnsureInitialized(null);
                return _path;
            }
        }

        internal static void Initialize(ManualLogSource log, string component)
        {
            EnsureInitialized(log);
            Info(component + " diagnostics initialized. LogPath='" + _path + "'.");
        }

        internal static void Debug(string message)
        {
            Write("DEBUG", message);
        }

        internal static void Info(string message)
        {
            Write("INFO", message);
        }

        internal static void Warning(string message)
        {
            Write("WARNING", message);
        }

        internal static void Error(string message)
        {
            Write("ERROR", message);
        }

        internal static void Error(string message, Exception ex)
        {
            Write("ERROR", message + Environment.NewLine + ex);
        }

        private static void EnsureInitialized(ManualLogSource log)
        {
            lock (InitSync)
            {
                if (log != null)
                    _bepLog = log;

                if (_initialized)
                    return;

                _path = Path.Combine(BepInEx.Paths.CachePath, "ValheimDiscordRelay.log");

                try
                {
                    Directory.CreateDirectory(BepInEx.Paths.CachePath);
                    _initialized = true;
                    EnsureWriterThreadStarted();
                }
                catch (Exception ex)
                {
                    // We cannot safely write to the diagnostics file yet.
                    try { _bepLog?.LogError("Could not initialize diagnostics log: " + ex); }
                    catch { }
                }
            }
        }

        private static void EnsureWriterThreadStarted()
        {
            if (_writerThread != null)
                return;

            lock (InitSync)
            {
                if (_writerThread != null)
                    return;

                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "ValheimDiscordRelay-Log"
                };
                _writerThread.Start();
            }
        }

        private static void Write(string level, string message)
        {
            EnsureInitialized(null);

            string line = DateTime.UtcNow.ToString("o") + " " +
                          Component + level + "-" + (message ?? string.Empty) +
                          Environment.NewLine;

            EnsureWriterThreadStarted();

            Enqueue(new LogEntry
            {
                Line = line,
                Level = level,
                Message = message
            });
        }

        private static void Enqueue(LogEntry entry)
        {
            try
            {
                _queue.Add(entry);
            }
            catch
            {
                // The queue only rejects entries once CompleteAdding() has
                // been called, which this class never does (see Shutdown()).
                // Swallow defensively so a logging call can never throw into
                // caller code.
            }
        }

        private static void WriterLoop()
        {
            foreach (LogEntry entry in _queue.GetConsumingEnumerable())
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(_path, entry.Line, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    try { _bepLog?.LogError("Could not write diagnostics log: " + ex); }
                    catch { }
                }

                MirrorToBepInEx(entry.Level, entry.Message);
            }
        }

        private static void MirrorToBepInEx(string level, string message)
        {
            try
            {
                ManualLogSource log = _bepLog;
                if (log == null)
                    return;

                switch (level)
                {
                    case "ERROR":
                        log.LogError(message ?? string.Empty);
                        break;
                    case "WARNING":
                        log.LogWarning(message ?? string.Empty);
                        break;
                    case "DEBUG":
                        log.LogDebug(message ?? string.Empty);
                        break;
                    // INFO is intentionally not mirrored, matching prior behavior:
                    // it is still always written to ValheimDiscordRelay.log above.
                }
            }
            catch { }
        }

        private static void RotateIfNeeded()
        {
            DateTime now = DateTime.UtcNow;

            if (now - _lastRotationCheck < TimeSpan.FromMinutes(1))
                return;

            _lastRotationCheck = now;

            try
            {
                if (File.Exists(_path))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(_path);
                    if (now - modified >= LogLifetime)
                        File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                try { _bepLog?.LogError("Could not rotate diagnostics log '" + _path + "': " + ex); }
                catch { }
            }
        }

        // Gives the background writer thread a brief window to drain
        // whatever is already queued. Deliberately does not call
        // CompleteAdding(): the Client and Server plugin components live in
        // the same DLL and can both be active in the same process (e.g. a
        // self-hosted game), so either one's OnDestroy could run first. The
        // queue is left open so whichever component tears down last can keep
        // logging right up to the end. Safe to call multiple times and from
        // either component.
        internal static void Shutdown()
        {
            try
            {
                SpinWait.SpinUntil(() => _queue.Count == 0, 1500);
            }
            catch { }
        }
    }
}
