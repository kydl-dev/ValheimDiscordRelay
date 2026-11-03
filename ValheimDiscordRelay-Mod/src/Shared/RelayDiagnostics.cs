using System;
using KydlCore.Common;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// The mod's log calls (about 300 of them, from the main thread and from worker threads) now go through the shared
    /// <see cref="ModLog"/>: INFO / WARNING / ERROR are always written, DEBUG only while [DebugLog] Enable is on, and a line repeated
    /// within 2.5 s is written once. The mod no longer keeps a log file of its own.
    /// </summary>
    internal static class RelayDiagnostics
    {
        internal static void Debug(string message) { ModLog.Verbose(message); }

        internal static void Info(string message) { ModLog.Info(message); }

        internal static void Warning(string message) { ModLog.Warn(message); }

        internal static void Error(string message) { ModLog.Error(message); }

        internal static void Error(string message, Exception ex) { ModLog.Error(message + Environment.NewLine + ex); }
    }
}
