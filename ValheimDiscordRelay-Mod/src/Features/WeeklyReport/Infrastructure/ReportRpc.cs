using System;
using System.Collections.Generic;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Server side of the weekly-report RPCs: receives what the clients measured and adds it to ReportStats.
    ///
    /// The client that owns a creature measures the damage dealt to it and the kill, and the dying player reports their own
    /// death; each sends it here. As with the boss damage report, the server trusts the connected game's numbers
    /// but never trusts the sender to be a stranger: unknown peers, other protocol versions and absurd sizes are dropped.
    ///
    /// It also answers the in-game report window: a player's game asks for a snapshot and gets the current numbers back
    /// (see ReportSnapshotCodec for what is sent). That request is rate limited per player.
    /// </summary>
    internal static class ReportRpc
    {
        /// <summary>The window asks at most every 3 seconds; anything faster than this from one player is dropped.</summary>
        private const double MinSnapshotIntervalSeconds = 1.0;

        private const int MaxRememberedPeers = 128;

        private static readonly Dictionary<long, DateTime> LastSnapshotRequestUtc = new();

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(ReportProtocol.ActivityRpcName, OnActivity);
            rpc.Register<ZPackage>(ReportProtocol.EventRpcName, OnEvent);
            rpc.Register<ZPackage>(ReportProtocol.SnapshotRequestRpcName, OnSnapshotRequest);
            RelayDiagnostics.Info("Registered RPC " + ReportProtocol.ActivityRpcName);
            RelayDiagnostics.Info("Registered RPC " + ReportProtocol.EventRpcName);
            RelayDiagnostics.Info("Registered RPC " + ReportProtocol.SnapshotRequestRpcName);
        }

        /// <summary>
        /// A player's report window asks for the current numbers. The answer goes back to that player only.
        /// It is sent even while the report or the window is switched off (with just a status), so the window can say why
        /// it has nothing to show instead of waiting for a reply that never comes.
        /// </summary>
        private static void OnSnapshotRequest(long sender, ZPackage package)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                if (ZNet.instance.GetPeer(sender) == null)
                {
                    RelayDiagnostics.Warning("[Report] Rejected snapshot request from unknown peer " + sender + ".");
                    return;
                }

                if (package.ReadInt() != ReportProtocol.ProtocolVersion)
                    return;

                if (!AllowSnapshotRequest(sender))
                {
                    RelayDiagnostics.Debug("[Report] Dropped a snapshot request from " + sender + ": too many requests.");
                    return;
                }

                ZPackage reply = new ZPackage();
                ReportSnapshotCodec.Write(reply, ReportSnapshotCodec.BuildCurrent());

                if (ZRoutedRpc.instance != null)
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReportProtocol.SnapshotReplyRpcName, new object[] { reply });
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Failed to answer a snapshot request.", ex);
            }
        }

        /// <summary>
        /// Tells every connected player's game that a new statistics period has started, so the games forget the highest
        /// hits they already sent (see ReportTracker.OnPeriodReset). Server only; a game that does not know the message
        /// (an older build) just ignores it. Players that connect later start with no memory anyway.
        /// </summary>
        internal static void BroadcastPeriodReset()
        {
            try
            {
                ZNet znet = ZNet.instance;
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (znet == null || rpc == null || !znet.IsServer())
                    return;

                foreach (ZNetPeer peer in znet.GetPeers())
                {
                    // A peer that is still handshaking has no uid yet; it connects with an empty memory anyway.
                    if (peer == null || peer.m_uid == 0L)
                        continue;

                    ZPackage package = new ZPackage();
                    package.Write(ReportProtocol.ProtocolVersion);
                    rpc.InvokeRoutedRPC(peer.m_uid, ReportProtocol.PeriodResetRpcName, new object[] { package });
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not tell the players that a new period started.", ex);
            }
        }

        /// <summary>
        /// One player cannot make the server build snapshots in a loop.
        /// </summary>
        private static bool AllowSnapshotRequest(long sender)
        {
            DateTime now = DateTime.UtcNow;

            DateTime last;
            if (LastSnapshotRequestUtc.TryGetValue(sender, out last) && (now - last).TotalSeconds < MinSnapshotIntervalSeconds)
                return false;

            // Players come and go all week: start over rather than let the table grow without limit.
            if (LastSnapshotRequestUtc.Count >= MaxRememberedPeers && !LastSnapshotRequestUtc.ContainsKey(sender))
                LastSnapshotRequestUtc.Clear();

            LastSnapshotRequestUtc[sender] = now;
            return true;
        }

        /// <summary>
        /// Batched damage dealt and mob kills, one entry per player.
        /// </summary>
        private static void OnActivity(long sender, ZPackage package)
        {
            if (!ReportConfig.On)
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
        /// A single event: a player death, a boss kill, a mob kill, a highest hit, the best hit a boss received or the best hit on a kind of mob.
        /// </summary>
        private static void OnEvent(long sender, ZPackage package)
        {
            if (!ReportConfig.On)
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
                bool carriesDamage = kind == ReportProtocol.EventHighestHit || kind == ReportProtocol.EventBossHit || kind == ReportProtocol.EventMobHit || kind == ReportProtocol.EventDeathHit;
                float hitDamage = carriesDamage ? package.ReadSingle() : 0f;

                if (kind == ReportProtocol.EventPlayerDeath)
                {
                    // The dying player is the sender, so their own connected character name is used, not anything they typed.
                    // key is the killer's prefab name (empty for the environment), bossName the cause of death in English.
                    ReportStats.RecordDeath(peer.m_playerName);
                    ReportStats.RecordMobDeath(peer.m_playerName, key, bossName);
                    ReportStats.RecordDeathCause(peer.m_playerName, bossName, key);
                    RelayDiagnostics.Info("[Report] Death received from '" + peer.m_playerName + "': cause='" + bossName + "', killer prefab='" + key + "'.");

                    // killer is the killer's object key when a boss did it (empty from an older game, and for every other death).
                    if (!string.IsNullOrWhiteSpace(killer))
                        ReportStats.RecordBossPlayerKill(key, killer);
                }
                else if (kind == ReportProtocol.EventBossKill)
                {
                    ReportStats.RecordBossKill(key, bossName, killer);
                }
                else if (kind == ReportProtocol.EventMobKill)
                {
                    // key is the mob's prefab name, bossName its name (English or a key, empty from an older build).
                    ReportStats.RecordMobKill(killer, key, bossName);
                }
                else if (kind == ReportProtocol.EventHighestHit)
                {
                    // The creature is measured by the game that owns it, which is not always the game of the player who landed
                    // the hit (two players fighting the same creature: only one of them owns it). So the hit belongs to the
                    // player named in the payload, like the damage and kills of the activity batches; the sender's own name is
                    // only the fallback when the payload has none.
                    string hitter = string.IsNullOrWhiteSpace(killer) ? peer.m_playerName : killer;
                    ReportStats.RecordHighestHit(hitter, hitDamage, key, bossName);
                }
                else if (kind == ReportProtocol.EventBossHit)
                {
                    // key is the boss's instance key, bossName the weapon. Credited to the player named in the payload, for the
                    // same reason as the highest hit above.
                    string hitter = string.IsNullOrWhiteSpace(killer) ? peer.m_playerName : killer;
                    ReportStats.RecordBossHit(key, hitter, hitDamage, bossName);
                }
                else if (kind == ReportProtocol.EventMobHit)
                {
                    // key is the mob's prefab name, bossName its name (English or a key). Credited to the player named in the
                    // payload, for the same reason as the highest hit above.
                    string hitter = string.IsNullOrWhiteSpace(killer) ? peer.m_playerName : killer;
                    ReportStats.RecordMobHit(hitter, key, bossName, hitDamage);
                }
                else if (kind == ReportProtocol.EventDeathHit)
                {
                    // The finishing hit of the death the sender reported just before: key is the damage type, bossName the cause of death
                    // exactly as that death sent it, killer the killer's prefab name (empty for the environment, and from an older game).
                    // The dying player is the sender, so their own connected name is used.
                    ReportStats.RecordDeathHit(peer.m_playerName, bossName, killer, hitDamage, key);
                    RelayDiagnostics.Info("[Report] Finishing hit received from '" + peer.m_playerName + "': cause='" + bossName + "', damage=" +
                                          Math.Round(hitDamage).ToString(System.Globalization.CultureInfo.InvariantCulture) + ", type='" + key + "'.");
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
