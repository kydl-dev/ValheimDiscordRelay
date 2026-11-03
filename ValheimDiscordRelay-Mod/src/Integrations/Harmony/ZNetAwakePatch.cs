using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Runs when the world (or the connection to a world) starts, before any player connects, and knows whether this game is the server:
    /// that is when the strict version check of <see cref="VersionEnforcement"/> is switched on or off.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ZNetAwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance)
        {
            try
            {
                VersionEnforcement.Apply(__instance.IsServer());
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not apply the version check: " + ex.Message);
            }
        }
    }
}