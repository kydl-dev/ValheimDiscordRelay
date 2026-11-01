using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report
{
    internal static partial class ReportTracker
    {
        private static void ReportBossKill(HitState state)
        {
            // Multi-phase bosses (Kall Fimbulbringer) die once per phase; only the final phase is a real kill.
            if (BossCatalog.IsIntermediatePhase(state.Prefab))
                return;

            string bossName;
            BossInfo info;
            if (BossCatalog.TryGet(state.Prefab, out info))
            {
                bossName = info.DisplayName;
            }
            else
            {
                // Not a boss the mod knows: the creature's English name (see ResolveEnemyName), never the sender's language.
                bossName = state.Enemy;
                if (string.IsNullOrWhiteSpace(bossName))
                    bossName = EnglishNames.CreatureToEnglish(state.Prefab, null);
                bossName = BossCatalog.CapitalizeFirst(bossName);
            }

            if (IsServerProcess())
            {
                ReportStats.RecordBossKill(state.Key, bossName, state.Attacker);
                return;
            }

            // The boss's best hit goes first (messages arrive in the order they were sent), so the kill finds it already there.
            FlushBossHits();

            SendEvent(ReportProtocol.EventBossKill, state.Key ?? string.Empty, bossName, state.Attacker);
        }

        /// <summary>
        /// A player hit a boss this game owns. Only the best hit per boss matters, so this keeps it for the next flush and sends
        /// nothing for a hit that does not beat the best one this game already sent for that boss.
        /// </summary>
        private static void ReportBossHit(HitState state, float damage)
        {
            if (string.IsNullOrEmpty(state.InstanceKey) || float.IsNaN(damage) || float.IsInfinity(damage))
                return;

            try
            {
                if (IsServerProcess())
                {
                    ReportStats.RecordBossHit(state.InstanceKey, state.Attacker, damage, state.Weapon);
                    return;
                }

                float sentBest;
                if (SentBossBest.TryGetValue(state.InstanceKey, out sentBest) && damage <= sentBest)
                    return;

                PendingBossHit pending;
                if (!PendingBossHits.TryGetValue(state.InstanceKey, out pending))
                {
                    if (PendingBossHits.Count >= MaxPendingPlayers)
                        return;

                    pending = new PendingBossHit();
                    PendingBossHits[state.InstanceKey] = pending;
                }

                if (damage <= pending.Damage)
                    return;

                pending.Damage = damage;
                pending.Player = state.Attacker;
                pending.Weapon = state.Weapon;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not record a boss hit.", ex);
            }
        }

        /// <summary>
        /// A player hit a mob (not a boss) this game owns. Only the best hit per player and kind of mob matters, so this keeps it
        /// for the next flush and sends nothing for a hit that does not beat the best one this game already sent.
        /// </summary>
        private static void ReportMobHit(HitState state, float damage)
        {
            if (string.IsNullOrEmpty(state.Attacker) || float.IsNaN(damage) || float.IsInfinity(damage))
                return;

            try
            {
                if (IsServerProcess())
                {
                    ReportStats.RecordMobHit(state.Attacker, state.Prefab, state.Enemy, damage);
                    return;
                }

                string key = MobHitKey(state.Attacker, state.Prefab);

                float sentBest;
                if (SentMobBest.TryGetValue(key, out sentBest) && damage <= sentBest)
                    return;

                PendingMobHit pending;
                if (!PendingMobHits.TryGetValue(key, out pending))
                {
                    if (PendingMobHits.Count >= MaxPendingMobHits)
                        return;

                    pending = new PendingMobHit { Player = state.Attacker, Prefab = state.Prefab ?? string.Empty };
                    PendingMobHits[key] = pending;
                }

                if (damage <= pending.Damage)
                    return;

                pending.Damage = damage;
                pending.Enemy = state.Enemy;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not record a mob hit.", ex);
            }
        }

        /// <summary>The dictionary key of a (player, kind of mob) pair. The unit separator cannot be part of a prefab name.</summary>
        private static string MobHitKey(string player, string prefab)
        {
            return (player ?? string.Empty) + "\u001f" + (prefab ?? string.Empty);
        }

        private static void ReportMobKill(string player, string mobPrefab, string mobName)
        {
            try
            {
                if (IsServerProcess())
                {
                    ReportStats.RecordMobKill(player, mobPrefab, mobName);
                    return;
                }
                SendEvent(ReportProtocol.EventMobKill, mobPrefab ?? string.Empty, mobName ?? string.Empty, player ?? string.Empty);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not record mob type kill.", ex);
            }
        }

        /// <summary>The hit being applied right now, between the RPC_Damage prefix and postfix.</summary>
        private sealed class HitState
        {
            public Character Target;
            public string Attacker;
            public float HealthBefore;
            public int Frame;
            public bool Finalized;
            public bool IsBoss;
            public string Prefab;
            public string Key;

            /// <summary>
            /// For a boss: the identity the server uses for the per-boss numbers (BossCatalog.InstanceKey), or null when the
            /// boss's object could not be identified.
            /// </summary>
            public string InstanceKey;

            /// <summary>The creature's name for the report: English, or a name key the server turns into English.</summary>
            public string Enemy;

            public string Weapon;

            /// <summary>
            /// The hit being applied. Valheim lowers it in place (resistances, backstab, stagger) before it takes the health off,
            /// so once the creature has died its total is the full damage of the blow, also the part that was more than the
            /// creature had left (see Process).
            /// </summary>
            public HitData Hit;
        }

        /// <summary>The best single hit of one player since this game last sent one (waiting for the next flush).</summary>
        private sealed class PendingHit
        {
            public float Damage;
            public string Weapon;
            public string Enemy;
        }

        /// <summary>The best hit one boss received since this game last sent one (waiting for the next flush).</summary>
        private sealed class PendingBossHit
        {
            public float Damage;
            public string Player;
            public string Weapon;
        }

        /// <summary>The best hit one player landed on one kind of mob since this game last sent one (waiting for the next flush).</summary>
        private sealed class PendingMobHit
        {
            public string Player;
            public string Prefab;
            public string Enemy;
            public float Damage;
        }

        private sealed class PendingActivity
        {
            public float Damage;
            public int Kills;
        }
    }
}
