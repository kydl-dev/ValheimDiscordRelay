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
    /// A player's highest hit is batched too, and only sent when it beats the best one this game already sent for that
    /// player (see QueueHighestHit); the server tells every game when a new statistics period starts so that memory is cleared.
    ///
    /// The best hit each player lands on each kind of mob (never a boss) is batched the same way (see ReportMobHit): only a hit that
    /// beats the one this game already sent for that player and kind of mob goes out, at most every five seconds.
    ///
    /// Names (mobs, killers, enemies, weapons) are English: this game resolves them when it can, and the server finishes the
    /// job (see EnglishNames), so a report never shows a prefab name such as "Greydwarf_Elite" or another player's language.
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

        private const float FlushIntervalSeconds = 5f;
        private const int MaxPendingPlayers = 32;
        private const int MaxRememberedBestHits = 64;

        // One entry per (player, kind of mob): a handful of players times the creatures of the game.
        private const int MaxPendingMobHits = 64;
        private const int MaxRememberedMobHits = 512;

        /// <summary>The weapon of a player with nothing in their hands (the game's own name for the fists).</summary>
        private const string UnarmedKey = "$item_unarmed";

        /// <summary>The cause of death recorded when another player landed the killing blow.</summary>
        private const string PlayerKillCause = "Killed by a player";

        private static HitState _current;
        private static readonly Dictionary<string, PendingActivity> Pending = new(StringComparer.Ordinal);

        // Highest hits. SentBestHit remembers, per player, the best hit this game has sent to the server in this session and
        // statistics period: a hit that does not beat it cannot change the server's record, so it is never sent. It is cleared
        // when the server says a new period started (OnPeriodReset) and when a new connection begins (ResetBestHitMemory).
        private static readonly Dictionary<string, PendingHit> PendingHits = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> SentBestHit = new(StringComparer.OrdinalIgnoreCase);
        private static float _nextFlushTime;

        // The best hit each boss received, by BossCatalog.InstanceKey. Same idea as the highest hits above: SentBossBest is what
        // this game has already told the server, so a weaker hit is never sent. Unlike the players' highest hits it is NOT
        // cleared when a statistics period starts: the server keeps the fights of bosses that are still alive across the
        // period change (see ReportStats), so this game must not forget what it sent for them. It is cleared on a new connection.
        private static readonly Dictionary<string, PendingBossHit> PendingBossHits = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> SentBossBest = new(StringComparer.Ordinal);

        // The best hit each player landed on each kind of mob (never a boss), by player + prefab (MobHitKey). Same idea as the
        // highest hits above: SentMobBest is what this game has already told the server, so a weaker hit is never sent. Cleared
        // when the server starts a new statistics period and when a new connection begins.
        private static readonly Dictionary<string, PendingMobHit> PendingMobHits = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> SentMobBest = new(StringComparer.OrdinalIgnoreCase);

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
                Frame = Time.frameCount,
                Weapon = ResolveHitWeapon(hit),
                Hit = hit
            };

            try { state.IsBoss = character.IsBoss(); }
            catch { state.IsBoss = false; }

            // Read everything about the creature now: killing it can destroy the object before the postfix runs.
            try { state.Prefab = BossCatalog.CleanPrefabName(character.gameObject.name); }
            catch { state.Prefab = BossCatalog.CleanPrefabName(character.name); }

            if (state.IsBoss)
            {
                long user;
                long id;
                string prefab;
                if (BossKillTracker.TryGetIdentity(character, out state.Key, out user, out id, out prefab))
                {
                    state.Prefab = prefab;
                    state.InstanceKey = BossCatalog.InstanceKey(prefab, state.Key);
                }
                else
                {
                    state.Key = null;
                    state.InstanceKey = null;
                }
            }

            state.Enemy = ResolveEnemyName(character, state.Prefab);

            _current = state;
        }

        /// <summary>
        /// The creature's name for the report. A known boss uses its catalogue name; anything else is the creature's name key
        /// (Character.m_name), turned into English text when this game can, otherwise passed on for the server to finish.
        /// Unlike GetHoverName it does not depend on the language of this game.
        /// </summary>
        private static string ResolveEnemyName(Character character, string prefab)
        {
            try
            {
                BossInfo boss;
                if (BossCatalog.TryGet(prefab, out boss))
                    return boss.DisplayName;

                return EnglishNames.CreatureFromGame(character.m_name, prefab);
            }
            catch
            {
                return EnglishNames.CreatureFromGame(null, prefab);
            }
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
        /// <param name="playerName">The local player's name.</param>
        /// <param name="killerPrefab">The killer's prefab name, or null when there is no killer (environment).</param>
        /// <param name="killerNameKey">The killer's Character.m_name (a localization key), or null; see EnglishNames.</param>
        /// <param name="deathText">The death message of the game, used to name an environmental cause.</param>
        /// <param name="killedByPlayer">True when another player landed the killing blow (PvP). That is not a mob: the death is
        /// counted as a cause ("Killed by a player") and nothing is added to the mobs-that-killed-players ranking.</param>
        /// <param name="killerObjectKey">The killer's object key (BossAnnouncer.MakeKey) when a boss killed the player, so the
        /// server can count the death for that boss; null otherwise.</param>
        /// <param name="finishingDamage">The total damage of the hit that killed the player; 0 when unknown.</param>
        /// <param name="finishingType">The damage type of that hit in English ("Slash", "Fire"...); null or empty when plain damage or unknown.</param>
        /// <param name="environmentCause">The English name of an environmental cause taken from the hit type ("Drowning", "Fall damage"...);
        /// null when a creature killed the player or the cause is unknown. Preferred over reading the death text.</param>
        internal static void OnLocalPlayerDeath(string playerName, string killerPrefab, string killerNameKey, string deathText, bool killedByPlayer, string killerObjectKey,
            float finishingDamage = 0f, string finishingType = null, string environmentCause = null)
        {
            try
            {
                if (!Collecting())
                    return;

                if (killedByPlayer)
                {
                    killerPrefab = null;
                    killerObjectKey = null;
                }

                string killerName = string.IsNullOrWhiteSpace(killerPrefab) ? string.Empty : EnglishNames.CreatureFromGame(killerNameKey, killerPrefab);
                string cause = killedByPlayer ? PlayerKillCause : ResolveDeathCause(killerName, deathText, environmentCause, playerName);

                RelayDiagnostics.Info("[Report] Death reported: player='" + playerName + "', cause='" + cause + "', killer prefab='" + (killerPrefab ?? string.Empty) +
                                      "', finishing hit=" + (finishingDamage > 0f ? Math.Round(finishingDamage).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (finishingType ?? string.Empty) : "unknown") + ".");

                if (IsServerProcess())
                {
                    ReportStats.RecordDeath(playerName);
                    ReportStats.RecordMobDeath(playerName, killerPrefab, killerName);
                    ReportStats.RecordDeathCause(playerName, cause, killerPrefab);
                    ReportStats.RecordDeathHit(playerName, cause, killerPrefab, finishingDamage, finishingType);

                    if (!string.IsNullOrWhiteSpace(killerObjectKey))
                        ReportStats.RecordBossPlayerKill(killerPrefab, killerObjectKey);

                    return;
                }

                // The last field is the killer's object key for a boss death (empty otherwise); a server of an older build ignores it.
                SendEvent(ReportProtocol.EventPlayerDeath, killerPrefab ?? string.Empty, cause, killerObjectKey ?? string.Empty);

                // The finishing hit of that death, as its own event (a server of an older build ignores it). The cause is sent exactly as
                // above, so the server files the hit under the same cause.
                if (finishingDamage > 0f)
                    SendDeathHit(cause, finishingType, finishingDamage, killerPrefab);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not report the player's death.", ex);
            }
        }

        /// <summary>
        /// The cause of death for the report: the killer's name (already English, or a key for the server to finish), or for a death
        /// without a killer the environmental cause the game's hit type gave ("Drowning", "Fall damage"...). Only when neither is
        /// known is the death text searched for a keyword, as a last resort (with the player's own name taken out of it first, so a
        /// player called "Firebeard" cannot turn a drowning into a fire death).
        /// </summary>
        private static string ResolveDeathCause(string killerName, string deathText, string environmentCause, string playerName)
        {
            string mob = BossCatalog.StripTags(killerName ?? string.Empty).Trim();
            if (mob.Length > 0)
                return BossCatalog.CapitalizeFirst(mob);

            string environment = BossCatalog.StripTags(environmentCause ?? string.Empty).Trim();
            if (environment.Length > 0)
                return BossCatalog.CapitalizeFirst(environment);

            string text = deathText ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(playerName))
                text = text.Replace(playerName, " ");
            text = text.ToLowerInvariant();

            if (text.Contains("drown")) return "Drowning";
            if (text.Contains("tree")) return "Falling tree";
            if (text.Contains("fall") || text.Contains("fell")) return "Fall damage";
            if (text.Contains("poison")) return "Poison";
            if (text.Contains("burn") || text.Contains("fire")) return "Burning";
            if (text.Contains("smoke")) return "Smoke";
            if (text.Contains("freez") || text.Contains("frost")) return "Freezing";
            if (text.Contains("starv")) return "Starvation";
            if (text.Contains("impact")) return "Impact damage";
            return "Unknown / environment";
        }

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

        /// <summary>
        /// The weapon of the attacking player, as a name for the report: English when this game can say it in English,
        /// otherwise the item's key, which the server turns into English when it records the hit (see EnglishNames).
        ///
        /// Character.RPC_Damage runs on the game that owns the creature, and the attacker is often another player. The
        /// equipment of a player is only loaded on that player's own game; on any other game the player's object has no
        /// weapon in hand, so asking it (GetCurrentWeapon) would always answer "unarmed". For another player the game reads
        /// what that player's game published in the player's ZDO instead: the items in the right and left hand.
        /// </summary>
        private static string ResolveHitWeapon(HitData hit)
        {
            try
            {
                if (hit == null || !hit.HaveAttacker())
                    return "Unknown weapon";

                Player attacker = hit.GetAttacker() as Player;

                // The attacker is this game's own player: the game knows exactly what is held.
                if (attacker != null && ReferenceEquals(attacker, Player.m_localPlayer))
                {
                    ItemDrop.ItemData weapon = attacker.GetCurrentWeapon();
                    if (weapon != null && weapon.m_shared != null)
                    {
                        string prefab = null;
                        try { prefab = weapon.m_dropPrefab != null ? weapon.m_dropPrefab.name : null; }
                        catch { }

                        string name = EnglishNames.FromGame(weapon.m_shared.m_name, prefab);
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
                else
                {
                    string name = ResolveWeaponFromZdo(hit.m_attacker);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch { }
            return "Unknown weapon";
        }

        /// <summary>
        /// The weapon another player holds, read from the item hashes that player's game keeps in the player's ZDO
        /// (the same values the game uses to show the item in the player's hands). Like Humanoid.GetCurrentWeapon, the
        /// right hand counts first, then the left (a bow is held in the left hand); with no weapon in either hand the player
        /// is fighting with the fists. Returns null when the player or the items cannot be found.
        /// </summary>
        private static string ResolveWeaponFromZdo(ZDOID attackerId)
        {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null)
                return null;

            ZDO zdo = zdoMan.GetZDO(attackerId);
            if (zdo == null)
                return null;

            int right = zdo.GetInt(ZDOVars.s_rightItem, 0);
            int left = zdo.GetInt(ZDOVars.s_leftItem, 0);

            string name = WeaponNameFromHash(right);
            if (name == null)
                name = WeaponNameFromHash(left);

            if (name != null)
                return name;

            // Something is held but the item is not known here (a modded item, or the item database is not ready): do not
            // call it fists.
            if (right != 0 || left != 0)
                return null;

            return EnglishNames.FromGame(UnarmedKey, null);
        }

        /// <summary>
        /// The report name of the weapon whose prefab has this stable hash, or null when the hash is 0, unknown, or an item
        /// that is not a weapon (a shield, for example).
        /// </summary>
        private static string WeaponNameFromHash(int hash)
        {
            if (hash == 0)
                return null;

            ObjectDB db = ObjectDB.instance;
            if (db == null)
                return null;

            GameObject prefab = db.GetItemPrefab(hash);
            if (prefab == null)
                return null;

            ItemDrop drop = prefab.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null || !drop.m_itemData.IsWeapon())
                return null;

            string name = EnglishNames.FromGame(drop.m_itemData.m_shared.m_name, prefab.name);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

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
