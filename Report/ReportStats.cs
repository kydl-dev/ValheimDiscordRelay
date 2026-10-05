using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Report
{
    /// <summary>One player's numbers for the current report period.</summary>
    internal sealed class ReportPlayer
    {
        public string Name;
        public bool Joined;
        public int MobKills;
        public int Deaths;
        public double Damage;
    }

    /// <summary>One boss kill in the current report period.</summary>
    internal sealed class ReportBossKill
    {
        public string Boss;
        public string Killer;
        public DateTime TimeUtc;
    }

    /// <summary>
    /// A frozen copy of the statistics, safe to format without holding any lock.
    /// </summary>
    internal sealed class ReportSnapshot
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public readonly List<ReportPlayer> Players = new List<ReportPlayer>();
        public readonly List<ReportBossKill> BossKills = new List<ReportBossKill>();

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
        private const int MaxMobKillsPerEntry = 10000;
        private const double MaxDamagePerEntry = 100000000.0;

        private static readonly object Sync = new object();

        private static readonly Dictionary<string, ReportPlayer> Players =
            new Dictionary<string, ReportPlayer>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<ReportBossKill> BossKills = new List<ReportBossKill>();

        /// <summary>Boss objects already counted, so a repeated report of the same kill is ignored.</summary>
        private static readonly HashSet<string> SeenBossKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> SeenBossKeyOrder = new Queue<string>();

        private static DateTime _periodStartUtc = DateTime.UtcNow;
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

        /// <summary>
        /// Counts one boss kill.
        /// </summary>
        /// <param name="key">Identity of the boss object (so the same kill is not counted twice); may be empty.</param>
        /// <param name="bossName">The boss's display name.</param>
        /// <param name="killer">The player who landed the killing blow; may be empty.</param>
        internal static void RecordBossKill(string key, string bossName, string killer)
        {
            if (!Collecting())
                return;

            string boss = BossCatalog.StripTags(bossName);
            if (boss.Length == 0)
                boss = "Unknown boss";
            if (boss.Length > ReportProtocol.MaxNameChars)
                boss = boss.Substring(0, ReportProtocol.MaxNameChars);

            string killerName = NormalizeName(killer) ?? string.Empty;

            lock (Sync)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    if (!SeenBossKeys.Add(key))
                        return;

                    SeenBossKeyOrder.Enqueue(key);
                    while (SeenBossKeyOrder.Count > MaxBossKeysRemembered)
                        SeenBossKeys.Remove(SeenBossKeyOrder.Dequeue());
                }

                if (BossKills.Count >= MaxBossKills)
                    return;

                BossKills.Add(new ReportBossKill { Boss = boss, Killer = killerName, TimeUtc = DateTime.UtcNow });
                _dirty = true;
            }

            RelayDiagnostics.Info("[Report] Boss kill recorded: '" + boss + "'" +
                                  (killerName.Length > 0 ? " (killing blow by '" + killerName + "')." : "."));
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

                foreach (ReportPlayer player in Players.Values)
                {
                    snapshot.Players.Add(new ReportPlayer
                    {
                        Name = player.Name,
                        Joined = player.Joined,
                        MobKills = player.MobKills,
                        Deaths = player.Deaths,
                        Damage = player.Damage
                    });
                }

                foreach (ReportBossKill kill in BossKills)
                {
                    snapshot.BossKills.Add(new ReportBossKill
                    {
                        Boss = kill.Boss,
                        Killer = kill.Killer,
                        TimeUtc = kill.TimeUtc
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
            {
                Players.Clear();
                BossKills.Clear();
                _periodStartUtc = DateTime.UtcNow;

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

            RelayDiagnostics.Info("[Report] New statistics period started.");
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
        //   player    <name>  <1 if joined, else 0>  <mob kills>  <deaths>  <damage>
        //   boss      <boss name>  <killer>  <UTC time>
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

                Players.Clear();
                BossKills.Clear();

                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length == 0)
                        continue;

                    if (parts[0] == "start" && parts.Length >= 2)
                    {
                        haveStart = TryParseUtc(parts[1], out start);
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
                    else if (parts[0] == "boss" && parts.Length >= 4 && BossKills.Count < MaxBossKills)
                    {
                        DateTime when;
                        if (!TryParseUtc(parts[3], out when))
                            when = DateTime.UtcNow;

                        BossKills.Add(new ReportBossKill
                        {
                            Boss = Uri.UnescapeDataString(parts[1]),
                            Killer = Uri.UnescapeDataString(parts[2]),
                            TimeUtc = when
                        });
                    }
                }

                if (!haveStart)
                {
                    RelayDiagnostics.Warning("[Report] '" + _path + "' has no period start; starting a new period.");
                    Players.Clear();
                    BossKills.Clear();
                    return false;
                }

                _periodStartUtc = start;
                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("[Report] Could not read '" + _path + "': " + ex.Message + " Starting a new period.");
                Players.Clear();
                BossKills.Clear();
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

                foreach (ReportPlayer player in Players.Values)
                {
                    sb.Append("player\t")
                      .Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(player.Joined ? '1' : '0').Append('\t')
                      .Append(player.MobKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(player.Deaths.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(player.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }

                foreach (ReportBossKill kill in BossKills)
                {
                    sb.Append("boss\t")
                      .Append(Uri.EscapeDataString(kill.Boss ?? string.Empty)).Append('\t')
                      .Append(Uri.EscapeDataString(kill.Killer ?? string.Empty)).Append('\t')
                      .Append(kill.TimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
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
