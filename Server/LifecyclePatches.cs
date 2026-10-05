using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimDiscordRelay.Report;

namespace ValheimDiscordRelay.Server
{
    [HarmonyPatch(typeof(Game), "Start")]
    internal static class GameStartPatch
    {
        private static void Postfix()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            ServerRelay.RegisterRpc();
            ServerRelay.NotifyServerLifecycle(ServerRelay.ConsumeCleanShutdownMarker() ? "restart" : "up");
        }
    }

    [HarmonyPatch]
    internal static class GameShutdownPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodBase method in typeof(Game).GetMethods(flags))
            {
                if (method.Name == "Shutdown")
                    yield return method;
            }
        }

        private static void Prefix()
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                {
                    // Everybody is about to disappear: that is the server going down, not a wave of players leaving.
                    PlayerNotifications.MarkShuttingDown();
                    ReportScheduler.OnServerShutdown();

                    ServerRelay.NotifyServerLifecycle("down");
                    ServerRelay.WriteCleanShutdownMarker();
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process server shutdown notification.", ex);
            }
        }
    }

    /// <summary>
    /// A world hosted from Valheim's own menu ends with Game.Logout rather than Game.Shutdown. The connected players are
    /// disconnected as part of it, which must not be announced as each of them leaving.
    /// </summary>
    [HarmonyPatch]
    internal static class GameLogoutPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodBase method in typeof(Game).GetMethods(flags))
            {
                if (method.Name == "Logout")
                    yield return method;
            }
        }

        private static void Prefix()
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                {
                    PlayerNotifications.MarkShuttingDown();
                    ReportScheduler.OnServerShutdown();
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process game logout.", ex);
            }
        }
    }

    /// <summary>
    /// Fires when the game drops a connected player (Log Out, quit to desktop, lost connection, kick). This is what
    /// produces the "had to leave us" message on both dedicated servers and worlds hosted from Valheim's own menu.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) })]
    internal static class ZNetDisconnectPatch
    {
        private static void Prefix(ZNetPeer peer)
        {
            PlayerNotifications.OnPeerDisconnected(peer);
        }
    }

    /// <summary>
    /// The ZNet itself is closing (the server is stopping, or the host of a game-hosted world is leaving). Every
    /// connected player is dropped as part of it, which must not be announced as each of them leaving.
    /// </summary>
    [HarmonyPatch]
    internal static class ZNetShutdownPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodBase method in typeof(ZNet).GetMethods(flags))
            {
                if (method.Name == "Shutdown" || method.Name == "ShutdownWithoutSave")
                    yield return method;
            }
        }

        private static void Prefix()
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                    PlayerNotifications.MarkShuttingDown();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process ZNet shutdown.", ex);
            }
        }
    }
}
