using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Registers the boss video RPCs once the game (and with it the routed-RPC object of this connection) exists.
/// </summary>
[HarmonyPatch(typeof(Game), "Start")]
internal static class BossVideoRpcRegistrationPatch
{
    private static void Postfix()
    {
        try
        {
            BossVideoRpc.Register();
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not register the boss video RPCs.", ex);
        }
    }
}
