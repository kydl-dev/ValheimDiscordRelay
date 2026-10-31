using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Makes a server refuse players whose copy of this mod is not exactly the server's version, and players who do not have
    /// the mod at all.
    ///
    /// The check itself is ServerSync's version handshake (Common/ServerSync/ConfigSync.cs): every game announces its version
    /// to the server when it connects (SyncedConfig: AnnounceVersion), and a server with RequireExactVersion on disconnects
    /// a player who announced nothing or another version, before the player gets into the world. The player sees Valheim's
    /// "Incompatible version" message and the server's log says who was refused and why.
    ///
    /// What this class decides is when the server is strict: only while this game is a server (a dedicated server, or a world
    /// hosted from the game's menu) and [Admin] Server - Require Same Version is on. A game that connects to somebody else's
    /// server is never strict, so a player who has the mod can still join servers that do not have it.
    /// </summary>
    internal static class VersionEnforcement
    {
        private static bool _reported;

        internal static void Initialize(Harmony harmony)
        {
            harmony.PatchAll(typeof(ZNetAwakePatch));

            // Takes effect for the next player to connect; players already inside are not affected.
            AdminConfig.RequireSameVersion.SettingChanged += delegate
            {
                try
                {
                    ZNet znet = ZNet.instance;
                    if (znet != null)
                        Apply(znet.IsServer());
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Warning("Could not apply 'Server - Require Same Version': " + ex.Message);
                }
            };
        }

        /// <summary>
        /// Switches the strict check on or off for the connections made from now on.
        /// </summary>
        /// <param name="isServer">True when this game is the server (dedicated or hosted from the menu).</param>
        internal static void Apply(bool isServer)
        {
            bool strict = isServer && ModSettings.On && AdminConfig.RequireSameVersion.Value;
            SyncedConfig.Sync.RequireExactVersion = strict;

            if (!isServer)
                return;

            if (!strict)
            {
                RelayDiagnostics.Info("Version check is OFF ('Server - Require Same Version' is false): players without this mod, or with another version of it, can join.");
                _reported = false;
                return;
            }

            // Once per server start is enough; the setting can be flipped again later and is then reported again.
            if (!_reported)
            {
                _reported = true;
                RelayDiagnostics.Info("Version check is ON: only players with " + SyncedConfig.Sync.DisplayName + " " + ValheimDiscordRelayPlugin.ModVersion +
                                      " can join. Players without the mod, or with another version, are refused (see 'Server - Require Same Version').");
            }
        }

        /// <summary>
        /// Runs when the world (or the connection to a world) starts, before any player connects, and knows whether this game is the server.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNetAwakePatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                try
                {
                    Apply(__instance.IsServer());
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Warning("Could not apply the version check: " + ex.Message);
                }
            }
        }
    }
}
