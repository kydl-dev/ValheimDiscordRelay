using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report;

/// <summary>Part 2 of ReportTracker (split out so the class stays under the size limit).</summary>
internal static partial class ReportTracker
{
    /// <summary>
    /// Sends the batched damage / kills / highest hits to the server about every five seconds. A damage / kills batch is
    /// only taken out of the queue when there is a server connection to send it on.
    /// </summary>
    internal static void Tick()
    {
        if (Pending.Count == 0 && PendingHits.Count == 0 && PendingBossHits.Count == 0 && PendingMobHits.Count == 0)
            return;

        float now = Time.unscaledTime;
        if (now < _nextFlushTime)
            return;

        _nextFlushTime = now + FlushIntervalSeconds;
        FlushNow();
    }

    /// <summary>
    /// Sends whatever is still batched right away (used when the game session is being torn down).
    /// </summary>
    internal static void FlushNow()
    {
        FlushActivity();
        FlushHighestHits();
        FlushBossHits();
        FlushMobHits();
    }

    /// <summary>
    /// The server started a new statistics period (see ReportWindowRpc): every player's highest hit starts from zero
    /// again, so the hits this game remembered sending must not hold back the first hits of the new period.
    /// </summary>
    internal static void OnPeriodReset()
    {
        SentBestHit.Clear();
        PendingHits.Clear();
        SentMobBest.Clear();
        PendingMobHits.Clear();
        RelayDiagnostics.Debug("[Report] New statistics period: forgot the highest hits sent so far.");
    }

    /// <summary>
    /// A new connection to a server begins: nothing is known about what that server has recorded.
    /// </summary>
    internal static void ResetBestHitMemory()
    {
        SentBestHit.Clear();
        PendingHits.Clear();
        SentBossBest.Clear();
        PendingBossHits.Clear();
        SentMobBest.Clear();
        PendingMobHits.Clear();

        // Damage and kills that could not be sent on the previous connection belong to that server's statistics.
        Pending.Clear();
    }

    // ---------------------------------------------------------------- internals

    private static bool Collecting()
    {
        return ReportConfig.On &&
               ReportConfig.ClientOn;
    }

    private static bool IsServerProcess()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    /// <summary>
    /// Credits the health this hit removed to the attacking player, and the kill if the creature died.
    /// </summary>
    /// <param name="state">The hit being applied.</param>
    /// <param name="dying">True when called from the OnDeath patch, i.e. the creature is certainly dying.</param>
    private static void Process(HitState state, bool dying)
    {
        try
        {
            float healthAfter = 0f;
            try { healthAfter = state.Target.GetHealth(); }
            catch { healthAfter = 0f; }

            float dealt = Mathf.Max(0f, state.HealthBefore - Mathf.Max(0f, healthAfter));
            bool died = dying || healthAfter <= 0f;

            // The size of the hit, for the "highest hit" numbers. For a hit that did not kill, it is the health removed. A killing
            // blow can only remove what the creature had left (a 200 damage hit on a creature with 30 health left removes 30), so
            // for a kill the full damage of the hit counts, like the damage number the player sees. Total Damage keeps counting
            // the health really removed.
            float hitSize = dealt;
            if (died)
                hitSize = Mathf.Max(dealt, FullHitDamage(state));

            // Before the kill is reported: the server then has the boss's best hit when it records the kill.
            if (state.IsBoss && hitSize > 0f)
                ReportBossHit(state, hitSize);
            else if (!state.IsBoss && hitSize > 0f)
                ReportMobHit(state, hitSize);

            int mobKills = 0;
            if (died)
            {
                if (state.IsBoss)
                    ReportBossKill(state);
                else
                {
                    mobKills = 1;
                    ReportMobKill(state.Attacker, state.Prefab, state.Enemy);
                }
            }

            if (hitSize > 0f)
            {
                if (IsServerProcess())
                    ReportStats.RecordHighestHit(state.Attacker, hitSize, state.Weapon, state.Enemy);
                else
                    QueueHighestHit(state.Attacker, hitSize, state.Weapon, state.Enemy);
            }

            AddActivity(state.Attacker, dealt, mobKills);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("[Report] Could not track damage.", ex);
        }
    }

    /// <summary>
    /// The total damage of the hit after the game applied its modifiers (0 when it cannot be read). Only meaningful once the
    /// creature has been hit; see HitState.Hit.
    /// </summary>
    private static float FullHitDamage(HitState state)
    {
        try
        {
            if (state.Hit == null)
                return 0f;

            float total = state.Hit.GetTotalDamage();
            return float.IsNaN(total) || float.IsInfinity(total) || total < 0f ? 0f : total;
        }
        catch
        {
            return 0f;
        }
    }

    private static void AddActivity(string player, float damage, int mobKills)
    {
        if (damage <= 0f && mobKills == 0)
            return;

        if (IsServerProcess())
        {
            ReportStats.RecordActivity(player, damage, mobKills);
            return;
        }

        PendingActivity pending;
        if (!Pending.TryGetValue(player, out pending))
        {
            if (Pending.Count >= MaxPendingPlayers)
                return;

            pending = new PendingActivity();
            Pending[player] = pending;
        }

        pending.Damage += damage;
        pending.Kills += mobKills;
    }

    /// <summary>
    /// Remembers a player's hit if it can matter: only a hit above the best one this game has already sent for that
    /// player can change the server's record, and of several such hits within one flush interval only the best is kept.
    /// The event goes out at the next flush (see FlushHighestHits), so a long fight costs at most one small message every
    /// few seconds, and none at all once the player's best hit is known to the server.
    /// </summary>
    private static void QueueHighestHit(string player, float damage, string weapon, string enemy)
    {
        if (string.IsNullOrEmpty(player) || damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage))
            return;

        float sentBest;
        if (SentBestHit.TryGetValue(player, out sentBest) && damage <= sentBest)
            return;

        PendingHit pending;
        if (!PendingHits.TryGetValue(player, out pending))
        {
            if (PendingHits.Count >= MaxPendingPlayers)
                return;

            pending = new PendingHit();
            PendingHits[player] = pending;
        }

        if (damage <= pending.Damage)
            return;

        pending.Damage = damage;
        pending.Weapon = weapon;
        pending.Enemy = enemy;
    }
}
