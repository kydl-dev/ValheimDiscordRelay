using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Report
{

    /// <summary>
    /// The server's running totals for the current week, kept in memory and mirrored to a small text file
    /// (BepInEx/cache/ValheimDiscordRelay.WeeklyReport.txt) so a server restart does not lose the week.
    ///
    /// Players are identified by their character name (case-insensitive), which is what every source reports:
    /// the connected peer for joins and deaths, the attacking Player for kills and damage.
    ///
    /// Every Record* method does nothing while [Weekly Report] Server - Enabled is off.
    /// </summary>
    internal static partial class ReportStats
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
        /// Loads the saved week (or starts a new one). Called once from the plugin Awake().
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
            return ReportConfig.On;
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

    }
}
