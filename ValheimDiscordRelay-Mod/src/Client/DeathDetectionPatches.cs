using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Patches every overload of Game.Logout/Game.Shutdown to mark session teardown.
    /// </summary>
    [HarmonyPatch]
    internal static class GameSessionShutdownPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            foreach (string name in new[] { "Logout", "Shutdown" })
            {
                foreach (MethodBase method in typeof(Game).GetMethods(flags))
                {
                    if (method.Name == name)
                        yield return method;
                }
            }
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            try
            {
                string methodName = __originalMethod == null
                    ? "unknown"
                    : __originalMethod.Name;

                SessionLifecycle.MarkTearingDown(methodName);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not mark game session teardown.", ex);
            }

            try
            {
                // Leaving right after a boss kill: send the held-back announcement now rather than lose it.
                BossKillTracker.FlushDeferredDeaths();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not flush the pending boss announcement.", ex);
            }

            try
            {
                // Leaving the game: send the weekly-report numbers still waiting in the batch.
                ValheimDiscordRelay.Report.ReportTracker.FlushNow();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not flush the pending report batch.", ex);
            }
        }
    }

    /// <summary>
    /// Catches local-player death via health reaching zero, regardless of the HitData path.
    /// </summary>
    [HarmonyPatch(typeof(Character), "SetHealth")]
    internal static class PlayerDeathStatePatch
    {
        private static void Postfix(Character __instance)
        {
            try
            {
                Player player = __instance as Player;
                if (player == null || player != Player.m_localPlayer)
                    return;

                ClientPlugin.CaptureService?.CheckLocalPlayerDeath(player);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process player death state.", ex);
            }
        }
    }

    /// <summary>
    /// Catches local-player death via Valheim's own single-fire OnDeath signal.
    /// </summary>
    [HarmonyPatch(typeof(Player), "OnDeath")]
    internal static class PlayerOnDeathPatch
    {
        private static void Prefix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer)
                    return;

                ClientPlugin.CaptureService?.CheckLocalPlayerDeath(__instance, true);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process Player.OnDeath.", ex);
            }
        }
    }

    [HarmonyPatch(typeof(Character), "Damage")]
    internal static class PlayerDamagePatch
    {
        private static void Postfix(Character __instance, HitData hit)
        {
            try
            {
                Player player = __instance as Player;
                if (player == null || player != Player.m_localPlayer)
                    return;

                ClientCaptureService service = ClientPlugin.CaptureService;
                if (service != null)
                    service.RecordLocalPlayerHit(hit);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not track local player damage.", ex);
            }
        }
    }
}
