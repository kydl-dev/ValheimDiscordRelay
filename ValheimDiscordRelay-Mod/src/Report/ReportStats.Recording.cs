using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Report
{
    internal static partial class ReportStats
    {
        // ---------------------------------------------------------------- recording

        internal static void RecordJoin(string name)
        {
            if (!Collecting())
                return;

            string clean = NormalizeName(name);
            if (clean == null)
                return;

            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null)
                    return;

                player.Joined = true;
                _dirty = true;
            }
        }

        internal static void RecordDeath(string name)
        {
            if (!Collecting())
                return;

            string clean = NormalizeName(name);
            if (clean == null)
                return;

            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null)
                    return;

                player.Deaths++;
                _dirty = true;
            }

            RelayDiagnostics.Debug("[Report] Death recorded for '" + clean + "'.");
        }

        /// <param name="cause">The cause of death: a creature's name (English, a name key or a prefab name, all turned into
        /// English here) or a plain English description such as "Drowning".</param>
        /// <param name="killerPrefab">The killer's prefab name, or null/empty for an environmental cause. It names a creature exactly like
        /// RecordMobDeath does (the built-in creature table), so the Death causes and Killer mobs tabs always call a creature the same.</param>
        internal static void RecordDeathCause(string playerName, string cause, string killerPrefab = null)
        {
            if (!Collecting()) return;
            string clean = NormalizeName(playerName);
            string label = CreatureLabel(cause, killerPrefab, "Unknown / environment");
            if (clean == null) return;
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null) return;
                AddCount(player.DeathCauses, label, 1);
                _dirty = true;
            }
        }

        /// <summary>
        /// The finishing hit of a death: the hit that took the player's health to zero. Only the biggest one per player and cause of
        /// death is kept. The cause is named exactly like the one of RecordDeathCause, so a cause and its finishing hit always meet
        /// under the same name.
        /// </summary>
        /// <param name="playerName">The player who died.</param>
        /// <param name="cause">The cause of death, as given to RecordDeathCause.</param>
        /// <param name="killerPrefab">The killer's prefab name, as given to RecordDeathCause (null/empty for the environment).</param>
        /// <param name="damage">The total damage of the finishing hit.</param>
        /// <param name="damageType">The damage type of the hit ("Slash", "Fire"...); may be empty.</param>
        internal static void RecordDeathHit(string playerName, string cause, string killerPrefab, double damage, string damageType)
        {
            if (!Collecting() || double.IsNaN(damage) || double.IsInfinity(damage) || damage <= 0.0) return;
            string clean = NormalizeName(playerName);
            if (clean == null) return;
            string label = CreatureLabel(cause, killerPrefab, "Unknown / environment");
            string type = NormalizeName(damageType) ?? string.Empty;
            if (type.Length > MaxDamageTypeChars) type = type.Substring(0, MaxDamageTypeChars);
            damage = Math.Min(damage, MaxDamagePerEntry);
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null) return;

                ReportDeathHit known;
                if (player.DeathCauseHits.TryGetValue(label, out known))
                {
                    if (damage <= known.Damage) return;
                    known.Damage = damage;
                    known.Type = type;
                }
                else if (player.DeathCauseHits.Count >= MaxDeathHitsPerPlayer)
                {
                    return;
                }
                else
                {
                    player.DeathCauseHits[label] = new ReportDeathHit { Damage = damage, Type = type };
                }

                _dirty = true;
            }
        }

        /// <param name="mobPrefab">The killer's prefab name; empty when nothing killed the player (environment).</param>
        /// <param name="mobName">The killer's name when the sender knows it (English or a name key); may be empty. Ignored when
        /// there is no killer prefab.</param>
        internal static void RecordMobDeath(string playerName, string mobPrefab, string mobName)
        {
            if (!Collecting()) return;
            string clean = NormalizeName(playerName);
            if (clean == null) return;
            // No killer prefab means nothing killed the player (drowning, falling...): that is a cause of death, not a mob.
            if (string.IsNullOrWhiteSpace(mobPrefab)) return;
            string mob = CreatureLabel(mobName, mobPrefab, "Unknown mob");
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null) return;
                AddCount(player.DeathsByMob, mob, 1);
                _dirty = true;
            }
        }

        /// <summary>
        /// Adds damage dealt and mob kills made by one player. Implausible values are clamped or ignored.
        /// </summary>
        internal static void RecordActivity(string name, float damage, int mobKills)
        {
            if (!Collecting())
                return;

            string clean = NormalizeName(name);
            if (clean == null)
                return;

            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage < 0f)
                damage = 0f;

            double dealt = Math.Min(damage, MaxDamagePerEntry);
            int kills = Math.Max(0, Math.Min(mobKills, MaxMobKillsPerEntry));

            if (dealt <= 0.0 && kills == 0)
                return;

            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null)
                    return;

                player.Damage += dealt;
                player.MobKills += kills;
                _dirty = true;
            }
        }

        internal static void RecordHighestHit(string playerName, double damage, string weapon, string enemy)
        {
            if (!Collecting() || double.IsNaN(damage) || double.IsInfinity(damage) || damage <= 0.0) return;
            string clean = NormalizeName(playerName);
            if (clean == null) return;
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null || damage <= player.HighestHitDamage) return;
                player.HighestHitDamage = Math.Min(damage, MaxDamagePerEntry);
                // Always stored as an English name, whatever language or form (key, prefab name) the sender's game used.
                player.HighestHitWeapon = NormalizeName(EnglishNames.ToEnglish(weapon)) ?? "Unknown weapon";
                // Same for the enemy: a name key or prefab name becomes the creature's English name.
                player.HighestHitEnemy = CreatureLabel(enemy, null, "Unknown enemy");
                if (player.HighestHitWeapon.Length > ReportProtocol.MaxNameChars) player.HighestHitWeapon = player.HighestHitWeapon.Substring(0, ReportProtocol.MaxNameChars);
                if (player.HighestHitEnemy.Length > ReportProtocol.MaxNameChars) player.HighestHitEnemy = player.HighestHitEnemy.Substring(0, ReportProtocol.MaxNameChars);
                _dirty = true;
            }
        }

        /// <param name="mobPrefab">The mob's prefab name (may carry "(Clone)").</param>
        /// <param name="mobName">The mob's name when the sender knows it (English or a name key); may be empty.</param>
        internal static void RecordMobKill(string playerName, string mobPrefab, string mobName)
        {
            if (!Collecting()) return;
            string clean = NormalizeName(playerName);
            if (clean == null) return;
            string mob = CreatureLabel(mobName, mobPrefab, "Unknown mob");
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null) return;
                AddCount(player.MobKillsByType, mob, 1);
                _dirty = true;
            }
        }

        /// <summary>
        /// A hit a player landed on a mob (not a boss). Only the biggest one per player and kind of mob is kept. The name is
        /// worked out exactly like the one of a mob kill (RecordMobKill), so the two always meet under the same key.
        /// </summary>
        /// <param name="playerName">The player who landed the hit.</param>
        /// <param name="mobPrefab">The mob's prefab name (may carry "(Clone)").</param>
        /// <param name="mobName">The mob's name when the sender knows it (English or a name key); may be empty.</param>
        /// <param name="damage">The health the hit removed.</param>
        internal static void RecordMobHit(string playerName, string mobPrefab, string mobName, double damage)
        {
            if (!Collecting() || double.IsNaN(damage) || double.IsInfinity(damage) || damage <= 0.0) return;
            string clean = NormalizeName(playerName);
            if (clean == null) return;
            string mob = CreatureLabel(mobName, mobPrefab, "Unknown mob");
            damage = Math.Min(damage, MaxDamagePerEntry);
            lock (Sync)
            {
                ReportPlayer player = GetOrAddLocked(clean);
                if (player == null) return;

                double best;
                if (player.MobBestHits.TryGetValue(mob, out best))
                {
                    if (damage <= best) return;
                }
                else if (player.MobBestHits.Count >= MaxMobHitsPerPlayer)
                {
                    return;
                }

                player.MobBestHits[mob] = damage;
                _dirty = true;
            }
        }

        /// <summary>
        /// Counts one boss kill. The numbers collected about this boss while it was alive (players it killed, the highest hit
        /// it received) are moved onto the kill.
        /// </summary>
        /// <param name="key">Identity of the boss object (so the same kill is not counted twice); may be empty.</param>
        /// <param name="bossName">The boss's display name.</param>
        /// <param name="killer">The player who landed the killing blow; may be empty.</param>
        internal static void RecordBossKill(string key, string bossName, string killer)
        {
            if (!Collecting())
                return;

            // Known bosses arrive with their English name; any other creature may arrive as a name key or prefab name.
            string boss = EnglishNames.CreatureToEnglish(BossCatalog.StripTags(bossName), null);
            if (boss.Length == 0)
                boss = "Unknown boss";
            if (boss.Length > ReportProtocol.MaxNameChars)
                boss = boss.Substring(0, ReportProtocol.MaxNameChars);

            string killerName = NormalizeName(killer) ?? string.Empty;

            // The same identity the players' games use for this boss when they report its hits and the players it killed.
            string instanceKey = BossCatalog.InstanceKeyForName(boss, key) ?? string.Empty;
            if (instanceKey.Length > ReportProtocol.MaxNameChars)
                instanceKey = string.Empty;

            int playerKills = 0;
            double hitDamage = 0.0;

            lock (Sync)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    if (!SeenBossKeys.Add(key))
                        return;

                    // Saved even when the kill list below is full, so the key is still remembered after a restart.
                    _dirty = true;
                }

                if (BossKills.Count >= MaxBossKills)
                    return;

                ReportBossKill row = new ReportBossKill { Boss = boss, Killer = killerName, TimeUtc = DateTime.UtcNow, InstanceKey = instanceKey };

                BossFight fight;
                if (instanceKey.Length > 0 && BossFights.TryGetValue(instanceKey, out fight))
                {
                    row.PlayerKills = fight.PlayerKills;
                    row.HighestHit = fight.HighestHit;
                    row.HighestHitBy = fight.HighestHitBy;
                    row.HighestHitWeapon = fight.HighestHitWeapon;
                    BossFights.Remove(instanceKey);
                }

                BossKills.Add(row);

                // The player who landed the killing blow always has a row, so the Players tab can show their boss kill.
                if (killerName.Length > 0)
                    GetOrAddLocked(killerName);

                playerKills = row.PlayerKills;
                hitDamage = row.HighestHit;
                _dirty = true;
            }

            RelayDiagnostics.Info("[Report] Boss kill recorded: '" + boss + "'" +
                                  (killerName.Length > 0 ? " (killing blow by '" + killerName + "')" : string.Empty) +
                                  ", players killed by it: " + playerKills.ToString(CultureInfo.InvariantCulture) +
                                  ", highest hit received: " + Math.Round(hitDamage).ToString(CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>
        /// A boss killed a player. Counted for that boss object (not for the boss type), every time: the same player dying to
        /// the same boss three times is three.
        /// </summary>
        /// <param name="killerPrefab">The killer's prefab name (a phase of a multi-phase boss shares one fight).</param>
        /// <param name="objectKey">The killer's object key (BossAnnouncer.MakeKey), reported by the dying player's game.</param>
        internal static void RecordBossPlayerKill(string killerPrefab, string objectKey)
        {
            if (!Collecting())
                return;

            string instanceKey = BossCatalog.InstanceKey(killerPrefab, (objectKey ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(instanceKey) || instanceKey.Length > ReportProtocol.MaxNameChars)
                return;

            lock (Sync)
            {
                ReportBossKill row = FindRecentBossKillLocked(instanceKey);
                if (row != null)
                    row.PlayerKills++;
                else
                    GetOrAddBossFightLocked(instanceKey).PlayerKills++;

                _dirty = true;
            }
        }

        /// <summary>
        /// A hit a boss received. Only the biggest one per boss is kept. Games report the best hit they measured, and a boss is
        /// measured by whichever game owns it at the moment, so this keeps the highest of everything it hears.
        /// </summary>
        /// <param name="instanceKey">BossCatalog.InstanceKey of the boss, as worked out by the game that measured the hit.</param>
        /// <param name="playerName">The player who landed the hit.</param>
        /// <param name="damage">The health the hit removed.</param>
        /// <param name="weapon">The weapon (English, or a key that is turned into English here).</param>
        internal static void RecordBossHit(string instanceKey, string playerName, double damage, string weapon)
        {
            if (!Collecting() || double.IsNaN(damage) || double.IsInfinity(damage) || damage <= 0.0)
                return;

            instanceKey = (instanceKey ?? string.Empty).Trim();
            if (instanceKey.Length == 0 || instanceKey.Length > ReportProtocol.MaxNameChars)
                return;

            damage = Math.Min(damage, MaxDamagePerEntry);
            string by = NormalizeName(playerName) ?? string.Empty;
            string weaponName = NormalizeName(EnglishNames.ToEnglish(weapon)) ?? "Unknown weapon";

            lock (Sync)
            {
                ReportBossKill row = FindRecentBossKillLocked(instanceKey);
                if (row != null)
                {
                    if (damage > row.HighestHit)
                    {
                        row.HighestHit = damage;
                        row.HighestHitBy = by;
                        row.HighestHitWeapon = weaponName;
                        _dirty = true;
                    }
                    return;
                }

                BossFight fight = GetOrAddBossFightLocked(instanceKey);
                if (damage > fight.HighestHit)
                {
                    fight.HighestHit = damage;
                    fight.HighestHitBy = by;
                    fight.HighestHitWeapon = weaponName;
                }

                _dirty = true;
            }
        }

        /// <summary>
        /// The newest boss kill of this boss that is recent enough to still take late news, or null. Caller must hold Sync.
        /// </summary>
        private static ReportBossKill FindRecentBossKillLocked(string instanceKey)
        {
            for (int i = BossKills.Count - 1; i >= 0; i--)
            {
                ReportBossKill kill = BossKills[i];
                if (!string.Equals(kill.InstanceKey, instanceKey, StringComparison.Ordinal))
                    continue;

                // The newest kill of this boss is the only one that can be meant.
                return (DateTime.UtcNow - kill.TimeUtc) <= BossLateUpdateWindow ? kill : null;
            }

            return null;
        }

        /// <summary>
        /// The fight of this boss, created when it is not known yet (the oldest fight makes room when the list is full).
        /// Caller must hold Sync.
        /// </summary>
        private static BossFight GetOrAddBossFightLocked(string instanceKey)
        {
            BossFight fight;
            if (!BossFights.TryGetValue(instanceKey, out fight))
            {
                PruneBossFightsLocked();

                if (BossFights.Count >= MaxBossFights)
                {
                    string oldestKey = null;
                    DateTime oldest = DateTime.MaxValue;
                    foreach (KeyValuePair<string, BossFight> entry in BossFights)
                    {
                        if (entry.Value.LastUpdateUtc < oldest)
                        {
                            oldest = entry.Value.LastUpdateUtc;
                            oldestKey = entry.Key;
                        }
                    }

                    if (oldestKey != null)
                        BossFights.Remove(oldestKey);
                }

                fight = new BossFight();
                BossFights[instanceKey] = fight;
            }

            fight.LastUpdateUtc = DateTime.UtcNow;
            return fight;
        }

        /// <summary>Forgets the fights that have had no news for BossFightMaxAge. Caller must hold Sync.</summary>
        private static void PruneBossFightsLocked()
        {
            if (BossFights.Count == 0)
                return;

            DateTime limit = DateTime.UtcNow - BossFightMaxAge;
            List<string> old = null;
            foreach (KeyValuePair<string, BossFight> entry in BossFights)
            {
                if (entry.Value.LastUpdateUtc < limit)
                {
                    if (old == null)
                        old = new List<string>();
                    old.Add(entry.Key);
                }
            }

            if (old == null)
                return;

            foreach (string key in old)
                BossFights.Remove(key);
        }
    }
}
