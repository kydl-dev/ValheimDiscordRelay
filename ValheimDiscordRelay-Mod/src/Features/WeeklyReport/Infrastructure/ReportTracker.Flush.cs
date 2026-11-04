using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report;

internal static partial class ReportTracker
{
    private static void FlushActivity()
    {
        if (Pending.Count == 0)
            return;

        // No connection to a server right now (loading screen, reconnecting): keep the batch and try again at the next
        // flush instead of throwing the numbers away. It is dropped when a new connection begins (ResetBestHitMemory),
        // because by then it would be credited to another server.
        if (!HasServerConnection())
            return;

        List<KeyValuePair<string, PendingActivity>> batch = new List<KeyValuePair<string, PendingActivity>>(Pending);
        Pending.Clear();

        try
        {
            ZPackage package = new ZPackage();
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(batch.Count);

            foreach (KeyValuePair<string, PendingActivity> entry in batch)
            {
                package.Write(entry.Key);
                package.Write(entry.Value.Damage);
                package.Write(entry.Value.Kills);
            }

            SendToServer(ReportProtocol.ActivityRpcName, package);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("[Report] Could not send the activity batch.", ex);
        }
    }

    /// <summary>
    /// Sends at most one highest-hit event per player (the best since the last flush), and remembers what was sent.
    /// </summary>
    private static void FlushHighestHits()
    {
        if (PendingHits.Count == 0)
            return;

        List<KeyValuePair<string, PendingHit>> batch = new List<KeyValuePair<string, PendingHit>>(PendingHits);
        PendingHits.Clear();

        foreach (KeyValuePair<string, PendingHit> entry in batch)
        {
            try
            {
                if (!SendHighestHit(entry.Key, entry.Value.Damage, entry.Value.Weapon, entry.Value.Enemy))
                    continue;

                // Only a hit that really left this game counts as known to the server.
                if (SentBestHit.Count >= MaxRememberedBestHits && !SentBestHit.ContainsKey(entry.Key))
                    SentBestHit.Clear();

                SentBestHit[entry.Key] = entry.Value.Damage;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not send a highest hit.", ex);
            }
        }
    }

    /// <summary>
    /// Sends the best hit of every boss that has one waiting, and remembers what was sent. Also called right before a boss kill
    /// is reported, so the server has the boss's best hit when it records the kill.
    /// </summary>
    private static void FlushBossHits()
    {
        if (PendingBossHits.Count == 0)
            return;

        List<KeyValuePair<string, PendingBossHit>> batch = new List<KeyValuePair<string, PendingBossHit>>(PendingBossHits);
        PendingBossHits.Clear();

        foreach (KeyValuePair<string, PendingBossHit> entry in batch)
        {
            try
            {
                if (!SendBossHit(entry.Key, entry.Value.Damage, entry.Value.Weapon, entry.Value.Player))
                    continue;

                // Only a hit that really left this game counts as known to the server.
                if (SentBossBest.Count >= MaxRememberedBestHits && !SentBossBest.ContainsKey(entry.Key))
                    SentBossBest.Clear();

                SentBossBest[entry.Key] = entry.Value.Damage;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not send a boss's highest hit.", ex);
            }
        }
    }

    /// <summary>
    /// Sends the best hit of every (player, kind of mob) that has one waiting, and remembers what was sent.
    /// </summary>
    private static void FlushMobHits()
    {
        if (PendingMobHits.Count == 0)
            return;

        List<KeyValuePair<string, PendingMobHit>> batch = new List<KeyValuePair<string, PendingMobHit>>(PendingMobHits);
        PendingMobHits.Clear();

        foreach (KeyValuePair<string, PendingMobHit> entry in batch)
        {
            try
            {
                PendingMobHit hit = entry.Value;
                if (!SendMobHit(hit.Prefab, hit.Enemy, hit.Player, hit.Damage))
                    continue;

                // Only a hit that really left this game counts as known to the server.
                if (SentMobBest.Count >= MaxRememberedMobHits && !SentMobBest.ContainsKey(entry.Key))
                    SentMobBest.Clear();

                SentMobBest[entry.Key] = hit.Damage;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not send a mob hit.", ex);
            }
        }
    }
}
