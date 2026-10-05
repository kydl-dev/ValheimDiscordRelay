using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Measures the health each hit removes from a creature, and who hit it.
    /// Character.RPC_Damage only runs on the creature's owner.
    /// </summary>
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class ReportDamagePatch
    {
        private static void Prefix(Character __instance, HitData hit)
        {
            try
            {
                ReportTracker.BeginDamage(__instance, hit);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not start damage tracking.", ex);
            }
        }

        private static void Postfix(Character __instance)
        {
            try
            {
                ReportTracker.EndDamage(__instance);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not finish damage tracking.", ex);
            }
        }
    }

    /// <summary>
    /// Credits the killing blow. A creature killed by the hit being applied is destroyed inside RPC_Damage, so the kill
    /// has to be counted here, while the creature can still be read.
    /// </summary>
    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class ReportCharacterDeathPatch
    {
        private static void Prefix(Character __instance)
        {
            try
            {
                ReportTracker.OnNativeDeath(__instance);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not process a creature death.", ex);
            }
        }
    }
}
