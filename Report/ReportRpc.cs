using System;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Server side of the weekly-report RPCs: receives what the clients measured and adds it to ReportStats.
    ///
    /// The client that owns a creature measures the damage dealt to it and the kill, and the dying player reports their own
    /// death; each sends it here. As with the boss damage report, the server trusts the connected game's numbers
    /// but never trusts the sender to be a stranger: unknown peers, other protocol versions and absurd sizes are dropped.
    /// </summary>
    internal static class ReportRpc
    {
        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(ReportProtocol.ActivityRpcName, OnActivity);
            rpc.Register<ZPackage>(ReportProtocol.EventRpcName, OnEvent);
            RelayDiagnostics.Info("Registered RPC " + ReportProtocol.ActivityRpcName);
            RelayDiagnostics.Info("Registered RPC " + ReportProtocol.EventRpcName);
        }

        /// <summary>
        /// Batched damage dealt and mob kills, one entry per player.
        /// </summary>
        private static void OnActivity(long sender, ZPackage package)
        {
            if (!ReportConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                if (ZNet.instance.GetPeer(sender) == null)
                {
                    RelayDiagnostics.Warning("[Report] Rejected activity report from unknown peer " + sender + ".");
                    return;
                }

                if (package.ReadInt() != ReportProtocol.ProtocolVersion)
                    return;

                int count = package.ReadInt();
                if (count < 0 || count > ReportProtocol.MaxEntriesPerMessage)
                {
                    RelayDiagnostics.Warning("[Report] Rejected activity report from " + sender + ": bad entry count " + count + ".");
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    string player = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    float damage = package.ReadSingle();
                    int kills = package.ReadInt();

                    ReportStats.RecordActivity(player, damage, kills);
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Failed to process activity report.", ex);
            }
        }

        /// <summary>
        /// A single event: a player death or a boss kill.
        /// </summary>
        private static void OnEvent(long sender, ZPackage package)
        {
            if (!ReportConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null)
                {
                    RelayDiagnostics.Warning("[Report] Rejected event report from unknown peer " + sender + ".");
                    return;
                }

                if (package.ReadInt() != ReportProtocol.ProtocolVersion)
                    return;

                int kind = package.ReadInt();
                string key = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                string bossName = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                string killer = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);

                if (kind == ReportProtocol.EventPlayerDeath)
                {
                    // The dying player is the sender, so their own connected character name is used, not anything they typed.
                    ReportStats.RecordDeath(peer.m_playerName);
                }
                else if (kind == ReportProtocol.EventBossKill)
                {
                    ReportStats.RecordBossKill(key, bossName, killer);
                }
                else
                {
                    RelayDiagnostics.Warning("[Report] Ignored event report from " + sender + ": unknown kind " + kind + ".");
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Failed to process event report.", ex);
            }
        }
    }
}
