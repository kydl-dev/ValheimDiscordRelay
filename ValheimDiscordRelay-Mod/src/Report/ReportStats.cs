using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Report
{
    /// <summary>The biggest single hit landed on one kind of mob, and who landed it.</summary>
    internal sealed class ReportMobHit
    {
        public double Damage;
        public string By = string.Empty;
    }

    /// <summary>The finishing hit (the hit that killed the player) of a cause of death: its total damage and its damage type.</summary>
    internal sealed class ReportDeathHit
    {
        public double Damage;

        /// <summary>The damage type that made up most of the hit ("Slash", "Fire"...); empty when it is plain/generic damage.</summary>
        public string Type = string.Empty;
    }

    /// <summary>One player's numbers for the current report period.</summary>
    internal sealed class ReportPlayer
    {
        public string Name;
        public bool Joined;
        public int MobKills;
        public int Deaths;
        public double Damage;
        public double HighestHitDamage;
        public string HighestHitWeapon = string.Empty;
        public string HighestHitEnemy = string.Empty;

        /// <summary>
        /// Bosses whose killing blow this player landed this period. Not stored in the running totals: it is counted from the
        /// boss kill list when a snapshot is taken (see ReportStats.Snapshot).
        /// </summary>
        public int BossesKilled;

        public readonly Dictionary<string, int> MobKillsByType = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathsByMob = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathCauses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The biggest single hit THIS player landed on each kind of mob (health removed), by the same English mob name as
        /// MobKillsByType. Bosses are not in here (they have their own numbers). Every kind of mob is tracked; the window only
        /// shows the entries of the mobs in the player's top five.
        /// </summary>
        public readonly Dictionary<string, double> MobBestHits = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The biggest finishing hit (the hit that killed this player) per cause of death, by the same name as DeathCauses.
        /// Every cause is tracked; the window only shows the entries of the causes in the player's top five.
        /// </summary>
        public readonly Dictionary<string, ReportDeathHit> DeathCauseHits = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One boss kill in the current report period.</summary>
    internal sealed class ReportBossKill
    {
        public string Boss;
        public string Killer;
        public DateTime TimeUtc;

        /// <summary>
        /// Identity of this boss for the numbers below (BossCatalog.InstanceKey). Empty for a kill saved by an older build.
        /// Not sent to the players' games.
        /// </summary>
        public string InstanceKey = string.Empty;

        /// <summary>
        /// How many times THIS boss killed a player before it died. The same player dying to it several times counts every
        /// time; deaths to another boss of the same kind (another instance) are not included.
        /// </summary>
        public int PlayerKills;

        /// <summary>The biggest single hit this boss received (health removed), who landed it and with what weapon. 0 = unknown.</summary>
        public double HighestHit;
        public string HighestHitBy = string.Empty;
        public string HighestHitWeapon = string.Empty;
    }

    /// <summary>
    /// A frozen copy of the statistics, safe to format without holding any lock.
    /// </summary>
    internal sealed class ReportSnapshot
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public readonly List<ReportPlayer> Players = new();
        public readonly List<ReportBossKill> BossKills = new();
        public readonly Dictionary<string, int> MobKillsByType = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathsByMob = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathCauses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of different players that joined during the period.</summary>
        public int JoinedCount
        {
            get
            {
                int count = 0;
                foreach (ReportPlayer player in Players)
                {
                    if (player.Joined)
                        count++;
                }
                return count;
            }
        }

        public int TotalMobKills
        {
            get
            {
                int total = 0;
                foreach (ReportPlayer player in Players)
                    total += player.MobKills;
                return total;
            }
        }

        public int TotalDeaths
        {
            get
            {
                int total = 0;
                foreach (ReportPlayer player in Players)
                    total += player.Deaths;
                return total;
            }
        }

        /// <summary>The player with the most damage dealt, or null if nobody dealt any.</summary>
        public ReportPlayer TopDamage()
        {
            ReportPlayer top = null;
            foreach (ReportPlayer player in Players)
            {
                if (player.Damage > 0.0 && (top == null || player.Damage > top.Damage))
                    top = player;
            }
            return top;
        }
    }

    /// <summary>
    /// The server's running totals for the current week, kept in memory and mirrored to a small text file
    /// (BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt) so a server restart does not lose the week.
    ///
    /// Players are identified by their character name (case-insensitive), which is what every source reports:
    /// the connected peer for joins and deaths, the attacking Player for kills and damage.
    ///
    /// Every Record* method does nothing while [Weekly Report] Server - Enabled is off.
    /// </summary>
    internal static class ReportStats
    {
        private const string FileName = "ValheimDiscordRelay.WeeklyReport.txt";
        private const string FileHeader = "ValheimDiscordRelay weekly report data v1";

        private const int MaxPlayers = 500;
        private const int MaxBossKills = 500;
        private const int MaxBossKeysRemembered = 128;

        /// <summary>A saved boss key older than this is not loaded again (see RememberedKeys).</summary>
        private static readonly TimeSpan BossKeyMaxAge = TimeSpan.FromHours(24);
        /// <summary>
        /// Bosses that have a fight in progress (players killed by them, hits they received) but have not been killed yet.
        /// A boss stays in the world after a wipe and is fought again, so these are kept across a period change and only
        /// forgotten after BossFightMaxAge without news or when the list is full.
        /// </summary>
        private const int MaxBossFights = 64;

        private static readonly TimeSpan BossFightMaxAge = TimeSpan.FromDays(14);

        /// <summary>
        /// Numbers about a boss that arrive after its kill was recorded (a player who died in the same moment, a highest hit
        /// reported by the game that owned the boss before) still go to that kill when they arrive this soon after it.
        /// </summary>
        private static readonly TimeSpan BossLateUpdateWindow = TimeSpan.FromMinutes(10);

        /// <summary>The most kinds of mob one player keeps a best hit for (there are well under a hundred in the game).</summary>
        private const int MaxMobHitsPerPlayer = 256;

        /// <summary>The most causes of death one player keeps a finishing hit for.</summary>
        private const int MaxDeathHitsPerPlayer = 256;

        /// <summary>The longest damage type text kept ("Lightning" is the longest the game has).</summary>
        private const int MaxDamageTypeChars = 24;

        private const int MaxMobKillsPerEntry = 10000;
        private const double MaxDamagePerEntry = 100000000.0;

        private static readonly object Sync = new();

        private static readonly Dictionary<string, ReportPlayer> Players = new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<ReportBossKill> BossKills = new();

        /// <summary>
        /// Boss objects already counted, so a repeated report of the same kill is ignored. Saved with the rest of the
        /// statistics (and kept across a period change on purpose), so a kill reported again after a server restart is not
        /// counted twice either.
        /// </summary>
        private static readonly RememberedKeys SeenBossKeys = new(MaxBossKeysRemembered, BossKeyMaxAge);

        /// <summary>The numbers collected about a boss that is still alive.</summary>
        private sealed class BossFight
        {
            public int PlayerKills;
            public double HighestHit;
            public string HighestHitBy = string.Empty;
            public string HighestHitWeapon = string.Empty;
            public DateTime LastUpdateUtc = DateTime.UtcNow;
        }

        /// <summary>Boss fights in progress, by BossCatalog.InstanceKey. Saved with the rest of the statistics.</summary>
        private static readonly Dictionary<string, BossFight> BossFights = new(StringComparer.Ordinal);

        private static DateTime _periodStartUtc = DateTime.UtcNow;

        /// <summary>
        /// The period start of the report Discord has accepted (DateTime.MinValue: none). Saved at once when Discord answers,
        /// so a server that stops between Discord's answer and the start of the new period does not post the same week again
        /// after the restart (see MarkReportDelivered and Initialize).
        /// </summary>
        private static DateTime _deliveredPeriodStartUtc = DateTime.MinValue;

        private static bool _dirty;
        private static string _path;

        // ---------------------------------------------------------------- lifecycle

        /// <summary>
        /// Loads the saved week (or starts a new one). Called once from ServerPlugin.Awake().
        /// </summary>
        internal static void Initialize()
        {
            lock (Sync)
            {
                // BepInEx/cache, next to the clean-shutdown marker: it is runtime data, not configuration.
                _path = Path.Combine(BepInEx.Paths.CachePath, FileName);

                try { Directory.CreateDirectory(BepInEx.Paths.CachePath); }
                catch (Exception ex) { RelayDiagnostics.Warning("[Report] Could not create '" + BepInEx.Paths.CachePath + "': " + ex.Message); }

                if (Load())
                {
                    RelayDiagnostics.Info("[Report] Loaded this week's statistics from '" + _path + "' (period started " +
                                          _periodStartUtc.ToString("u", CultureInfo.InvariantCulture) + ").");

                    if (_deliveredPeriodStartUtc == _periodStartUtc)
                    {
                        // Discord accepted this week's report, but the server stopped before the new period was started.
                        // Posting it again would be a duplicate; what was counted after the report was built is not
                        // counted in either period (same as without a restart).
                        RelayDiagnostics.Info("[Report] The saved period was already reported to Discord; starting a new period instead of sending it again.");
                        StartNewPeriodLocked(null);
                    }
                }
                else
                {
                    _periodStartUtc = DateTime.UtcNow;
                    _dirty = true;
                    SaveLocked();
                    RelayDiagnostics.Info("[Report] Started a new statistics period.");
                }
            }
        }

        /// <summary>When the current report period began.</summary>
        internal static DateTime PeriodStartUtc
        {
            get
            {
                lock (Sync)
                    return _periodStartUtc;
            }
        }

        /// <summary>Writes the totals to disk if anything changed since the last write.</summary>
        internal static void SaveIfDirty()
        {
            lock (Sync)
            {
                if (_dirty)
                    SaveLocked();
            }
        }

        /// <summary>
        /// Discord accepted the weekly report that covered the period starting at <paramref name="periodStartUtc"/>.
        /// Called on the webhook thread. The fact is written to disk right away: the statistics themselves are only reset
        /// later, on the main thread, and a server that stops in between would otherwise send the same report again.
        /// Ignored when that period is no longer the current one.
        /// </summary>
        internal static void MarkReportDelivered(DateTime periodStartUtc)
        {
            lock (Sync)
            {
                if (periodStartUtc != _periodStartUtc)
                    return;

                _deliveredPeriodStartUtc = periodStartUtc;
                _dirty = true;
                SaveLocked();
            }
        }

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

        // ---------------------------------------------------------------- reading / resetting

        /// <summary>
        /// A copy of the totals as they stand now. Does not change anything.
        /// </summary>
        internal static ReportSnapshot Snapshot()
        {
            lock (Sync)
            {
                ReportSnapshot snapshot = new ReportSnapshot
                {
                    StartUtc = _periodStartUtc,
                    EndUtc = DateTime.UtcNow
                };

                // Bosses killed per player: the boss kills where that player landed the killing blow (names are case-insensitive,
                // like everywhere else).
                Dictionary<string, int> bossesKilledBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ReportBossKill kill in BossKills)
                {
                    if (string.IsNullOrEmpty(kill.Killer))
                        continue;

                    int killed;
                    bossesKilledBy.TryGetValue(kill.Killer, out killed);
                    bossesKilledBy[kill.Killer] = killed + 1;
                }

                foreach (ReportPlayer player in Players.Values)
                {
                    int bossesKilled;
                    bossesKilledBy.TryGetValue(player.Name, out bossesKilled);

                    snapshot.Players.Add(new ReportPlayer
                    {
                        Name = player.Name,
                        Joined = player.Joined,
                        MobKills = player.MobKills,
                        Deaths = player.Deaths,
                        Damage = player.Damage,
                        BossesKilled = bossesKilled,
                        HighestHitDamage = player.HighestHitDamage,
                        // Weapons saved by an earlier version may still be keys or prefab names: shown in English here.
                        HighestHitWeapon = EnglishNames.ToEnglish(player.HighestHitWeapon),
                        HighestHitEnemy = player.HighestHitEnemy
                    });
                    foreach (KeyValuePair<string, int> mob in player.MobKillsByType)
                    {
                        snapshot.Players[snapshot.Players.Count - 1].MobKillsByType[mob.Key] = mob.Value;
                        int total; snapshot.MobKillsByType.TryGetValue(mob.Key, out total);
                        snapshot.MobKillsByType[mob.Key] = total + mob.Value;
                    }
                    foreach (KeyValuePair<string, int> mob in player.DeathsByMob)
                    {
                        snapshot.Players[snapshot.Players.Count - 1].DeathsByMob[mob.Key] = mob.Value;
                        int total; snapshot.DeathsByMob.TryGetValue(mob.Key, out total);
                        snapshot.DeathsByMob[mob.Key] = total + mob.Value;
                    }
                    foreach (KeyValuePair<string, int> cause in player.DeathCauses)
                    {
                        snapshot.Players[snapshot.Players.Count - 1].DeathCauses[cause.Key] = cause.Value;
                        int total; snapshot.DeathCauses.TryGetValue(cause.Key, out total);
                        snapshot.DeathCauses[cause.Key] = total + cause.Value;
                    }
                    foreach (KeyValuePair<string, double> hit in player.MobBestHits)
                        snapshot.Players[snapshot.Players.Count - 1].MobBestHits[hit.Key] = hit.Value;
                    foreach (KeyValuePair<string, ReportDeathHit> hit in player.DeathCauseHits)
                        snapshot.Players[snapshot.Players.Count - 1].DeathCauseHits[hit.Key] = new ReportDeathHit { Damage = hit.Value.Damage, Type = hit.Value.Type };
                }

                foreach (ReportBossKill kill in BossKills)
                {
                    snapshot.BossKills.Add(new ReportBossKill
                    {
                        Boss = kill.Boss,
                        Killer = kill.Killer,
                        TimeUtc = kill.TimeUtc,
                        InstanceKey = kill.InstanceKey,
                        PlayerKills = kill.PlayerKills,
                        HighestHit = kill.HighestHit,
                        HighestHitBy = kill.HighestHitBy,
                        HighestHitWeapon = kill.HighestHitWeapon
                    });
                }

                return snapshot;
            }
        }

        /// <summary>
        /// Starts a new period now. Everybody currently online is counted as having joined it, so a player who simply stays
        /// connected across the weekly cut-off still appears in the next report.
        /// </summary>
        /// <param name="onlineNames">Names of the players online right now.</param>
        internal static void RestartPeriod(IEnumerable<string> onlineNames)
        {
            lock (Sync)
                StartNewPeriodLocked(onlineNames);

            RelayDiagnostics.Info("[Report] New statistics period started.");

            // Outside the lock: the players' games forget the highest hits they already sent (see ReportTracker).
            ReportRpc.BroadcastPeriodReset();
        }

        /// <summary>Clears the totals, starts a period now and saves. Caller must hold Sync. Sends nothing.</summary>
        private static void StartNewPeriodLocked(IEnumerable<string> onlineNames)
        {
            Players.Clear();
            BossKills.Clear();

            // BossFights is kept on purpose: a boss that was fought but not killed this week is still the same boss next week,
            // and the players it killed count for the kill (like SeenBossKeys, which also survives a period change).
            _periodStartUtc = DateTime.UtcNow;
            _deliveredPeriodStartUtc = DateTime.MinValue;

            if (onlineNames != null)
            {
                foreach (string name in onlineNames)
                {
                    string clean = NormalizeName(name);
                    if (clean == null)
                        continue;

                    ReportPlayer player = GetOrAddLocked(clean);
                    if (player != null)
                        player.Joined = true;
                }
            }

            _dirty = true;
            SaveLocked();
        }

        // ---------------------------------------------------------------- helpers

        private static bool Collecting()
        {
            return ReportConfig.Enabled != null && ReportConfig.Enabled.Value;
        }

        /// <summary>
        /// Strips rich-text tags, the zero-width space the mod itself inserts into "@everyone", and surrounding blanks.
        /// Returns null for an empty name.
        /// </summary>
        internal static string NormalizeName(string raw)
        {
            string name = BossCatalog.StripTags(raw).Replace("\u200b", string.Empty).Trim();
            if (name.Length == 0)
                return null;

            if (name.Length > ReportProtocol.MaxNameChars)
                name = name.Substring(0, ReportProtocol.MaxNameChars);

            return name;
        }

        /// <summary>
        /// The English name of a creature (or an environmental cause) for the rankings: capped, tag-free and never empty.
        /// </summary>
        /// <param name="name">English text, a name key ("$enemy_..."), a prefab name ("Greydwarf_Elite"), or empty.</param>
        /// <param name="prefab">The prefab name, used when the name is empty or a key this game cannot resolve; may be null.</param>
        /// <param name="fallback">What to return when there is nothing to name (may be null).</param>
        private static string CreatureLabel(string name, string prefab, string fallback)
        {
            string source = string.IsNullOrWhiteSpace(name) ? prefab : name;
            string label = NormalizeName(EnglishNames.CreatureToEnglish(source, prefab));
            return label == null ? fallback : BossCatalog.CapitalizeFirst(label);
        }

        private static void AddCount(Dictionary<string, int> counts, string key, int amount)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + amount;
        }

        /// <summary>Returns the player's row, creating it if there is room. Caller must hold Sync.</summary>
        private static ReportPlayer GetOrAddLocked(string cleanName)
        {
            ReportPlayer player;
            if (Players.TryGetValue(cleanName, out player))
                return player;

            if (Players.Count >= MaxPlayers)
                return null;

            player = new ReportPlayer { Name = cleanName };
            Players[cleanName] = player;
            return player;
        }

        // ---------------------------------------------------------------- persistence (plain tab-separated text)
        //
        //   line 1:   header
        //   start     <UTC time, round-trip format>
        //   reported  <UTC time, same as start>   (present only when Discord accepted the report of that period; builds before this line existed ignore it)
        //   player    <name>  <1 if joined, else 0>  <mob kills>  <deaths>  <damage>
        //   hit       <n>  <highest hit damage>  <weapon>  <enemy>
        //   mobkill   <n>  <mob>  <count>
        //   mobdeath  <n>  <mob>  <count>
        //   deathcause <n>  <cause>  <count>
        //   mobhit    <n>  <mob>  <highest hit on that mob>   (builds before this line existed ignore it)
        //   deathhit  <n>  <cause>  <highest finishing hit>  <its damage type, may be empty>   (builds before this line existed ignore it)
        //   boss      <boss name>  <killer>  <UTC time>  <instance key>  <players it killed>  <highest hit>  <hit by>  <hit weapon>
        //             (the last five only in files of this build and later; older builds read the first four and ignore the rest)
        //   bossfight <instance key>  <players it killed>  <highest hit>  <hit by>  <hit weapon>  <UTC time of the last news>
        //             (a boss that was fought but not killed yet; builds before this line existed ignore it)
        //   bosskey   <boss object key>  <UTC time it was counted>   (oldest first; builds before this line existed ignore it)
        //
        // Names are URL-escaped, so they can never contain a tab or a line break.

        /// <summary>Caller must hold Sync. Returns false if there is no usable file.</summary>
        private static bool Load()
        {
            try
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                    return false;

                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), FileHeader, StringComparison.Ordinal))
                {
                    RelayDiagnostics.Warning("[Report] '" + _path + "' is not a weekly report data file; starting a new period.");
                    return false;
                }

                bool haveStart = false;
                DateTime start = DateTime.UtcNow;
                DateTime reported = DateTime.MinValue;

                Players.Clear();
                BossKills.Clear();
                SeenBossKeys.Clear();
                BossFights.Clear();

                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length == 0)
                        continue;

                    if (parts[0] == "start" && parts.Length >= 2)
                    {
                        haveStart = TryParseUtc(parts[1], out start);
                    }
                    else if (parts[0] == "reported" && parts.Length >= 2)
                    {
                        if (!TryParseUtc(parts[1], out reported))
                            reported = DateTime.MinValue;
                    }
                    else if (parts[0] == "player" && parts.Length >= 6)
                    {
                        string name = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        if (name == null)
                            continue;

                        ReportPlayer player = GetOrAddLocked(name);
                        if (player == null)
                            continue;

                        int kills;
                        int deaths;
                        double damage;
                        int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out kills);
                        int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out deaths);
                        double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out damage);

                        player.Joined = parts[2] == "1";
                        player.MobKills = Math.Max(0, kills);
                        player.Deaths = Math.Max(0, deaths);
                        player.Damage = (double.IsNaN(damage) || double.IsInfinity(damage) || damage < 0.0) ? 0.0 : damage;
                    }
                    else if (parts[0] == "hit" && parts.Length >= 5)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        double hitDamage; double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage);
                        if (playerName != null && hitDamage > 0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            if (player != null)
                            {
                                player.HighestHitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                                player.HighestHitWeapon = Uri.UnescapeDataString(parts[3]);
                                // A file from an earlier build may hold a prefab name or key: shown in English from now on.
                                player.HighestHitEnemy = CreatureLabel(Uri.UnescapeDataString(parts[4]), null, "Unknown enemy");
                            }
                        }
                    }
                    else if (parts[0] == "mobkill" && parts.Length >= 4)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        string mobName = Uri.UnescapeDataString(parts[2]);
                        int count;
                        if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            if (player != null)
                            {
                                // Files from an earlier build hold prefab names ("Greydwarf_Elite"): converted to English here.
                                // Two old names can become the same English name, so the counts are added, not replaced.
                                string label = CreatureLabel(mobName, null, null);
                                if (label != null)
                                    AddCount(player.MobKillsByType, label, count);
                            }
                        }
                    }
                    else if (parts[0] == "mobdeath" && parts.Length >= 4)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        string mobName = Uri.UnescapeDataString(parts[2]);
                        int count;
                        if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            if (player != null)
                            {
                                string label = CreatureLabel(mobName, null, null);
                                if (label != null)
                                    AddCount(player.DeathsByMob, label, count);
                            }
                        }
                    }
                    else if (parts[0] == "mobhit" && parts.Length >= 4)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        string mobName = Uri.UnescapeDataString(parts[2]);
                        double hitDamage;
                        if (playerName != null &&
                            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage) &&
                            hitDamage > 0.0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            string label = CreatureLabel(mobName, null, null);
                            if (player != null && label != null)
                            {
                                // Two saved names can become the same English name: the higher hit wins.
                                double known;
                                hitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                                if (!player.MobBestHits.TryGetValue(label, out known) || hitDamage > known)
                                    player.MobBestHits[label] = hitDamage;
                            }
                        }
                    }
                    else if (parts[0] == "deathhit" && parts.Length >= 5)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        string cause = Uri.UnescapeDataString(parts[2]);
                        string type = NormalizeName(Uri.UnescapeDataString(parts[4])) ?? string.Empty;
                        double hitDamage;
                        if (playerName != null &&
                            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage) &&
                            hitDamage > 0.0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            string label = CreatureLabel(cause, null, null);
                            if (player != null && label != null)
                            {
                                // Two saved names can become the same English name: the higher hit wins.
                                hitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                                if (type.Length > MaxDamageTypeChars) type = type.Substring(0, MaxDamageTypeChars);
                                ReportDeathHit known;
                                if (!player.DeathCauseHits.TryGetValue(label, out known))
                                    player.DeathCauseHits[label] = new ReportDeathHit { Damage = hitDamage, Type = type };
                                else if (hitDamage > known.Damage)
                                {
                                    known.Damage = hitDamage;
                                    known.Type = type;
                                }
                            }
                        }
                    }
                    else if (parts[0] == "deathcause" && parts.Length >= 4)
                    {
                        string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                        string cause = Uri.UnescapeDataString(parts[2]); int count;
                        if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                        {
                            ReportPlayer player = GetOrAddLocked(playerName);
                            if (player != null)
                            {
                                string label = CreatureLabel(cause, null, null);
                                if (label != null)
                                    AddCount(player.DeathCauses, label, count);
                            }
                        }
                    }
                    else if (parts[0] == "boss" && parts.Length >= 4 && BossKills.Count < MaxBossKills)
                    {
                        DateTime when;
                        if (!TryParseUtc(parts[3], out when))
                            when = DateTime.UtcNow;

                        ReportBossKill kill = new ReportBossKill
                        {
                            Boss = Uri.UnescapeDataString(parts[1]),
                            Killer = Uri.UnescapeDataString(parts[2]),
                            TimeUtc = when
                        };

                        // Fields 4-8 exist in files written by this build and later; a file from an earlier build has only the first
                        // four and loads with the per-boss numbers unknown (0 / empty).
                        if (parts.Length >= 9)
                        {
                            kill.InstanceKey = Uri.UnescapeDataString(parts[4]);

                            int playerKills;
                            double hit;
                            int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerKills);
                            double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out hit);

                            kill.PlayerKills = Math.Max(0, Math.Min(playerKills, MaxMobKillsPerEntry));
                            kill.HighestHit = (double.IsNaN(hit) || double.IsInfinity(hit) || hit < 0.0) ? 0.0 : Math.Min(hit, MaxDamagePerEntry);
                            kill.HighestHitBy = Uri.UnescapeDataString(parts[7]);
                            kill.HighestHitWeapon = Uri.UnescapeDataString(parts[8]);
                        }

                        BossKills.Add(kill);
                    }
                    else if (parts[0] == "bossfight" && parts.Length >= 7 && BossFights.Count < MaxBossFights)
                    {
                        string instanceKey = Uri.UnescapeDataString(parts[1]);
                        if (instanceKey.Length > 0 && instanceKey.Length <= ReportProtocol.MaxNameChars)
                        {
                            int playerKills;
                            double hit;
                            DateTime updated;
                            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerKills);
                            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hit);
                            if (!TryParseUtc(parts[6], out updated))
                                updated = DateTime.UtcNow;

                            BossFights[instanceKey] = new BossFight
                            {
                                PlayerKills = Math.Max(0, Math.Min(playerKills, MaxMobKillsPerEntry)),
                                HighestHit = (double.IsNaN(hit) || double.IsInfinity(hit) || hit < 0.0) ? 0.0 : Math.Min(hit, MaxDamagePerEntry),
                                HighestHitBy = Uri.UnescapeDataString(parts[4]),
                                HighestHitWeapon = Uri.UnescapeDataString(parts[5]),
                                LastUpdateUtc = updated
                            };
                        }
                    }
                    else if (parts[0] == "bosskey" && parts.Length >= 3)
                    {
                        // Entries too old, unreadable or repeated are skipped by the helper.
                        SeenBossKeys.TryLoad(Uri.UnescapeDataString(parts[1]), parts[2]);
                    }
                }

                if (!haveStart)
                {
                    RelayDiagnostics.Warning("[Report] '" + _path + "' has no period start; starting a new period.");
                    Players.Clear();
                    BossKills.Clear();
                    SeenBossKeys.Clear();
                    BossFights.Clear();
                    return false;
                }

                PruneBossFightsLocked();

                _periodStartUtc = start;
                _deliveredPeriodStartUtc = reported;
                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("[Report] Could not read '" + _path + "': " + ex.Message + " Starting a new period.");
                Players.Clear();
                BossKills.Clear();
                SeenBossKeys.Clear();
                BossFights.Clear();
                return false;
            }
        }

        /// <summary>Caller must hold Sync.</summary>
        private static void SaveLocked()
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                StringBuilder sb = new StringBuilder(1024);
                sb.Append(FileHeader).Append('\n');
                sb.Append("start\t").Append(_periodStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
                if (_deliveredPeriodStartUtc == _periodStartUtc)
                    sb.Append("reported\t").Append(_deliveredPeriodStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                {
                    sb.Append("player\t")
                      .Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(player.Joined ? '1' : '0').Append('\t')
                      .Append(player.MobKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(player.Deaths.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(player.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }

                foreach (ReportPlayer player in Players.Values)
                    if (player.HighestHitDamage > 0)
                        sb.Append("hit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(player.HighestHitDamage.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(Uri.EscapeDataString(player.HighestHitWeapon ?? string.Empty)).Append('\t')
                          .Append(Uri.EscapeDataString(player.HighestHitEnemy ?? string.Empty)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                    foreach (KeyValuePair<string, int> mob in player.MobKillsByType)
                        sb.Append("mobkill\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(Uri.EscapeDataString(mob.Key)).Append('\t').Append(mob.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                    foreach (KeyValuePair<string, int> mob in player.DeathsByMob)
                        sb.Append("mobdeath\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(Uri.EscapeDataString(mob.Key)).Append('\t').Append(mob.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                    foreach (KeyValuePair<string, double> hit in player.MobBestHits)
                        sb.Append("mobhit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(Uri.EscapeDataString(hit.Key)).Append('\t').Append(hit.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                    foreach (KeyValuePair<string, ReportDeathHit> hit in player.DeathCauseHits)
                        sb.Append("deathhit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(Uri.EscapeDataString(hit.Key)).Append('\t')
                          .Append(hit.Value.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(Uri.EscapeDataString(hit.Value.Type ?? string.Empty)).Append('\n');

                foreach (ReportPlayer player in Players.Values)
                    foreach (KeyValuePair<string, int> cause in player.DeathCauses)
                        sb.Append("deathcause\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                          .Append(Uri.EscapeDataString(cause.Key)).Append('\t').Append(cause.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

                foreach (ReportBossKill kill in BossKills)
                {
                    sb.Append("boss\t")
                      .Append(Uri.EscapeDataString(kill.Boss ?? string.Empty)).Append('\t')
                      .Append(Uri.EscapeDataString(kill.Killer ?? string.Empty)).Append('\t')
                      .Append(kill.TimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Uri.EscapeDataString(kill.InstanceKey ?? string.Empty)).Append('\t')
                      .Append(kill.PlayerKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kill.HighestHit.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Uri.EscapeDataString(kill.HighestHitBy ?? string.Empty)).Append('\t')
                      .Append(Uri.EscapeDataString(kill.HighestHitWeapon ?? string.Empty)).Append('\n');
                }

                foreach (KeyValuePair<string, BossFight> fight in BossFights)
                {
                    sb.Append("bossfight\t")
                      .Append(Uri.EscapeDataString(fight.Key)).Append('\t')
                      .Append(fight.Value.PlayerKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(fight.Value.HighestHit.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Uri.EscapeDataString(fight.Value.HighestHitBy ?? string.Empty)).Append('\t')
                      .Append(Uri.EscapeDataString(fight.Value.HighestHitWeapon ?? string.Empty)).Append('\t')
                      .Append(fight.Value.LastUpdateUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
                }

                // Oldest first, so loading them in file order keeps the newest ones if there are more than the limit.
                foreach (KeyValuePair<string, DateTime> seen in SeenBossKeys.Entries)
                {
                    sb.Append("bosskey\t")
                      .Append(Uri.EscapeDataString(seen.Key)).Append('\t')
                      .Append(RememberedKeys.FormatTime(seen.Value)).Append('\n');
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(temp, _path, true);
                File.Delete(temp);

                _dirty = false;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("[Report] Could not save '" + _path + "': " + ex.Message);
            }
        }

        private static bool TryParseUtc(string text, out DateTime value)
        {
            return DateTime.TryParse(
                text == null ? null : text.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
        }
    }
}
