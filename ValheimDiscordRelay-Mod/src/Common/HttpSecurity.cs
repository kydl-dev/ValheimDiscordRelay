using System;
using System.Net;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// Makes sure outgoing HTTPS requests can use TLS 1.2 (the old Mono runtime in Unity does not enable it by default,
    /// and Discord requires it) without taking over the process-wide setting from the other mods in the game.
    /// </summary>
    internal static class HttpSecurity
    {
        private static readonly object Sync = new object();
        private static volatile bool _done;

        /// <summary>
        /// Adds TLS 1.2 to <see cref="ServicePointManager.SecurityProtocol"/> if it is not already enabled.
        /// Every protocol another mod (or the game) has enabled stays enabled. Safe to call from any thread and as often as
        /// you like: it only touches the setting the first time.
        /// </summary>
        internal static void EnsureTls12()
        {
            if (_done)
                return;

            lock (Sync)
            {
                if (_done)
                    return;

                try
                {
                    SecurityProtocolType current = ServicePointManager.SecurityProtocol;
                    if ((current & SecurityProtocolType.Tls12) == 0)
                        ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Warning("Could not enable TLS 1.2 for Discord requests: " + ex.Message);
                }

                _done = true;
            }
        }
    }
}
