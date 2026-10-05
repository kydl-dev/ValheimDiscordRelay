using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Measures what the weekly report needs, in the game that has the authority to see it:
    ///
    ///  - Damage dealt to creatures, and mob kills: Valheim only applies damage on the game that OWNS the creature, so that
    ///    game measures how much health each player's hits removed (after resistances) and who landed the killing blow.
    ///  - Boss kills: the same measurement, reported as its own event (the final phase only, for multi-phase bosses).
    ///  - The local player's own deaths.
    ///
    /// Damage and mob kills are batched and sent to the server every few seconds; boss kills and deaths are sent at once.
    /// When this game IS the server (dedicated server, or the host of a hosted world) the numbers go straight to ReportStats.
    ///
    /// What counts: hits by a player on a creature that is not a player, not tamed, and (for the mob-kill number) not a boss.
    /// Creatures killed by a tamed animal, by another creature or by the environment are not credited to anyone.
    /// Like boss damage, it is only measured when the game that owns the creature runs this mod.
    /// </summary>
    internal static class ReportTracker
    {
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
            public string HoverName;
        }

        private sealed class PendingActivity
        {
            public float Damage;
            public int Kills;
        }

        private const float FlushIntervalSeconds = 5f;
        private const int MaxPendingPlayers = 32;

        private static HitState _current;
        private static readonly Dictionary<string, PendingActivity> Pending =
            new Dictionary<string, PendingActivity>(StringComparer.Ordinal);
        private static float _nextFlushTime;

        // ---------------------------------------------------------------- hooks called by the Harmony patches

        /// <summary>
        /// Called before Character.RPC_Damage: remembers the creature's health so the damage can be measured afterwards.
        /// Does nothing unless a player is hitting a creature this game owns.
        /// </summary>
        internal static void BeginDamage(Character character, HitData hit)
        {
            _current = null;

            if (character == null || character is Player || !Collecting())
                return;

            try
            {
                if (!character.IsOwner() || character.IsDead() || character.IsTamed())
                    return;
            }
            catch
            {
                return;
            }

            string attacker = BossKillTracker.ResolvePlayerAttacker(hit);
            if (attacker == null)
                return;

            HitState state = new HitState
            {
                Target = character,
                Attacker = attacker,
                HealthBefore = character.GetHealth(),
                Frame = Time.frameCount
            };

            try { state.IsBoss = character.IsBoss(); }
            catch { state.IsBoss = false; }

            if (state.IsBoss)
            {
                // Read everything about a boss now: killing it can destroy the object before the postfix runs.
                long user;
                long id;
                if (!BossKillTracker.TryGetIdentity(character, out state.Key, out user, out id, out state.Prefab))
                {
                    state.Key = null;
                    state.Prefab = BossCatalog.CleanPrefabName(character.gameObject.name);
                }

                try { state.HoverName = BossCatalog.StripTags(character.GetHoverName() ?? string.Empty); }
                catch { state.HoverName = string.Empty; }
            }

            _current = state;
        }

        /// <summary>
        /// Called after Character.RPC_Damage: credits the damage if the creature survived the hit.
        /// </summary>
        internal static void EndDamage(Character character)
        {
            HitState state = _current;
            _current = null;

            if (state == null || state.Finalized || !ReferenceEquals(state.Target, character))
                return;

            state.Finalized = true;

            // A creature that died was already handled by OnNativeDeath; one that vanished some other way is not credited.
            if (state.Target == null)
                return;

            Process(state, false);
        }

        /// <summary>
        /// Called when a creature is about to die. For a creature killed by the hit being applied right now this credits
        /// the damage and the kill (the object may be destroyed before RPC_Damage's postfix runs).
        /// </summary>
        internal static void OnNativeDeath(Character character)
        {
            if (character == null || character is Player)
                return;

            HitState state = _current;
            if (state == null || state.Finalized || !ReferenceEquals(state.Target, character) || state.Frame != Time.frameCount)
                return;

            state.Finalized = true;
            Process(state, true);
        }

        /// <summary>
        /// Called when the local player has died (once per death).
        /// </summary>
        internal static void OnLocalPlayerDeath(string playerName)
        {
            try
            {
                if (!Collecting())
                    return;

                if (IsServerProcess())
                {
                    ReportStats.RecordDeath(playerName);
                    return;
                }

                SendEvent(ReportProtocol.EventPlayerDeath, string.Empty, string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not report the player's death.", ex);
            }
        }

        /// <summary>
        /// Sends the batched damage / kills to the server about every five seconds.
        /// </summary>
        internal static void Tick()
        {
            if (Pending.Count == 0)
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
            if (Pending.Count == 0)
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

        // ---------------------------------------------------------------- internals

        private static bool Collecting()
        {
            return ReportConfig.Enabled != null && ReportConfig.Enabled.Value &&
                   ReportConfig.ClientEnabled != null && ReportConfig.ClientEnabled.Value;
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

                int mobKills = 0;
                if (died)
                {
                    if (state.IsBoss)
                        ReportBossKill(state);
                    else
                        mobKills = 1;
                }

                AddActivity(state.Attacker, dealt, mobKills);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not track damage.", ex);
            }
        }

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
                bossName = state.HoverName;
                if (string.IsNullOrWhiteSpace(bossName))
                    bossName = BossCatalog.CleanPrefabName(state.Prefab);
                bossName = BossCatalog.CapitalizeFirst(bossName);
            }

            if (IsServerProcess())
            {
                ReportStats.RecordBossKill(state.Key, bossName, state.Attacker);
                return;
            }

            SendEvent(ReportProtocol.EventBossKill, state.Key ?? string.Empty, bossName, state.Attacker);
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

        private static void SendToServer(string rpcName, ZPackage package)
        {
            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null)
                    return;

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null)
                    return;

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    serverPeer.m_uid,
                    rpcName,
                    new object[] { package });
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not send a report to the server.", ex);
            }
        }
    }
}
