using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Measures how much of the boss's health each hit removes (the real amount after resistances).
/// Character.RPC_Damage only runs on the boss's owner.
/// </summary>
[HarmonyPatch(typeof(Character), "RPC_Damage")]
internal static class BossDamagePatch
{
    private static void Prefix(Character __instance, HitData hit)
    {
        try
        {
            BossKillTracker.BeginDamage(__instance, hit);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not start boss damage tracking.", ex);
        }
    }

    private static void Postfix(Character __instance)
    {
        try
        {
            BossKillTracker.EndDamage(__instance);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not track boss damage.", ex);
        }
    }
}

/// <summary>
/// Fallback boss-death trigger for deaths that do not pass through RPC_Damage.
/// </summary>
[HarmonyPatch(typeof(Character), "OnDeath")]
internal static class BossOnDeathPatch
{
    private static void Prefix(Character __instance)
    {
        try
        {
            BossKillTracker.OnNativeDeath(__instance);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not process boss death.", ex);
        }
    }
}
