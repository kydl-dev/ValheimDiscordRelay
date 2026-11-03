using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report
{
    internal static partial class ReportTracker
    {
        /// <summary>Returns false when the event could not be sent (no connection to a server).</summary>
        private static bool SendHighestHit(string player, float damage, string weapon, string enemy)
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(ReportProtocol.EventHighestHit);
            package.Write(weapon ?? string.Empty);
            package.Write(enemy ?? string.Empty);
            package.Write(player ?? string.Empty);
            package.Write(damage);
            return SendToServer(ReportProtocol.EventRpcName, package);
        }

        /// <summary>Returns false when the event could not be sent (no connection to a server).</summary>
        private static bool SendMobHit(string prefab, string enemy, string player, float damage)
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(ReportProtocol.EventMobHit);
            package.Write(prefab ?? string.Empty);
            package.Write(enemy ?? string.Empty);
            package.Write(player ?? string.Empty);
            package.Write(damage);
            return SendToServer(ReportProtocol.EventRpcName, package);
        }

        /// <summary>Returns false when the event could not be sent (no connection to a server).</summary>
        private static bool SendBossHit(string instanceKey, float damage, string weapon, string player)
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(ReportProtocol.EventBossHit);
            package.Write(instanceKey ?? string.Empty);
            package.Write(weapon ?? string.Empty);
            package.Write(player ?? string.Empty);
            package.Write(damage);
            return SendToServer(ReportProtocol.EventRpcName, package);
        }

        /// <summary>
        /// The finishing hit of a death: key = damage type, bossName = cause of death, killer = the killer's prefab name (empty for the
        /// environment), so the server files the hit under exactly the name it gave the death (see ReportStats.RecordDeathHit).
        /// </summary>
        private static void SendDeathHit(string cause, string damageType, float damage, string killerPrefab)
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(ReportProtocol.EventDeathHit);
            package.Write(damageType ?? string.Empty);
            package.Write(cause ?? string.Empty);
            package.Write(killerPrefab ?? string.Empty);
            package.Write(damage);
            SendToServer(ReportProtocol.EventRpcName, package);
        }

        private static void SendEvent(int kind, string key, string bossName, string killer)
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(kind);
            package.Write(key ?? string.Empty);
            package.Write(bossName ?? string.Empty);
            package.Write(killer ?? string.Empty);

            SendToServer(ReportProtocol.EventRpcName, package);
        }

        /// <summary>True when there is a server peer a report could be sent to right now.</summary>
        private static bool HasServerConnection()
        {
            try
            {
                return ZNet.instance != null && ZRoutedRpc.instance != null && ZNet.instance.GetServerPeer() != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns true when the message was handed to the network layer.</summary>
        private static bool SendToServer(string rpcName, ZPackage package)
        {
            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null)
                    return false;

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null)
                    return false;

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    serverPeer.m_uid,
                    rpcName,
                    new object[] { package });
                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not send a report to the server.", ex);
                return false;
            }
        }
    }
}
