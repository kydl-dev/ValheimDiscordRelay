using System.Threading;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Synchronously latches session teardown so death detection cannot race Player destruction.
    /// </summary>
    internal static class SessionLifecycle
    {
        private static int _tearingDown;

        internal static bool IsTearingDown
        {
            get { return Volatile.Read(ref _tearingDown) != 0; }
        }

        internal static void MarkTearingDown(string reason)
        {
            if (Interlocked.Exchange(ref _tearingDown, 1) == 0)
            {
                RelayDiagnostics.Debug(
                    "[DeathVideo] Session teardown detected (" + reason +
                    "); suppressing death detection.");
            }
        }

        internal static void ResetForNewSession()
        {
            if (Interlocked.Exchange(ref _tearingDown, 0) != 0)
                RelayDiagnostics.Debug("[DeathVideo] New local-player session detected; death detection re-enabled.");
        }
    }
}
