using System;
using System.Collections.Generic;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// What the in-game report window shows: the weekly statistics as they stand right now, plus the few facts around them
    /// (server name, when the next report is due). Built on the server from ReportStats and sent to the player's game as a
    /// snapshot reply; on the host of a game-hosted world it is built locally and never sent.
    ///
    /// The player and boss lists are capped (ReportProtocol.MaxSnapshotPlayers / MaxSnapshotBossKills) so a reply stays
    /// small; the totals are always complete, and HiddenPlayers says how many rows were left out. Every ranking table
    /// (mobs killed, mobs that killed players, causes of death; combined and per player) holds only its top
    /// ReportProtocol.MaxSnapshotRankEntries entries, because the window shows nothing more. The combined tables are summed
    /// over ALL players before they are cut, so they are complete rankings.
    /// </summary>
    internal sealed class ReportWindowData
    {
        public int Status;
        public string ServerName = string.Empty;

        public DateTime StartUtc;
        public DateTime EndUtc;
        public DateTime NextDueUtc;

        public int JoinedTotal;
        public int KillsTotal;
        public int DeathsTotal;
        public int BossTotal;
        public int HiddenPlayers;

        /// <summary>
        /// True when the numbers per boss (players it killed, highest hit it received) and each player's bosses killed came with
        /// the reply. A server of an older build does not send them; the window then falls back on what it can work out itself.
        /// </summary>
        public bool HasBossDetails;

        /// <summary>
        /// True when the best hit per kind of mob came with the reply (a server of an older build does not send it; the Mobs killed
        /// tab then shows "-" for the hits).
        /// </summary>
        public bool HasMobHitDetails;

        /// <summary>
        /// True when MobMostDeaths came with the reply (or was worked out locally). A server of an older build does not send it;
        /// the Killer mobs tab then shows "-" in its "Most deaths by whom" column.
        /// </summary>
        public bool HasKillerMobDetails;

        /// <summary>
        /// True when the finishing hits and DeathCauseMostBy came with the reply (or were worked out locally). A server of an older
        /// build does not send them; the Death causes tab then shows "-" in those columns.
        /// </summary>
        public bool HasDeathCauseDetails;

        public string TopDamageName = string.Empty;
        public double TopDamage;

        public readonly List<ReportPlayer> Players = new();
        public readonly List<ReportBossKill> BossKills = new();
        public readonly Dictionary<string, int> MobKillsByType = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathsByMob = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DeathCauses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// For the mobs in MobKillsByType (the combined top five): the biggest single hit any player landed on that kind of mob and
        /// who landed it. The same for one player's own top five is in ReportPlayer.MobBestHits.
        /// </summary>
        public readonly Dictionary<string, ReportMobHit> MobBestHits = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// For the mobs in DeathsByMob (the combined top five, bosses included): the name of the player who died to that mob the
        /// most times. Key = the same mob name as in DeathsByMob. When several players are tied for the most deaths, all of them
        /// are named (at most ReportProtocol.MaxMostDeathsNames, then "+N more"), alphabetically. Read by the Killer mobs tab.
        /// </summary>
        public readonly Dictionary<string, string> MobMostDeaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// For the causes in DeathCauses (the combined top five): the biggest finishing hit any player died of with that cause. The
        /// same for one player's own top five is in ReportPlayer.DeathCauseHits.
        /// </summary>
        public readonly Dictionary<string, ReportDeathHit> DeathCauseHits = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// For the causes in DeathCauses (the combined top five): the name of the player who died of that cause the most times
        /// (ties named like MobMostDeaths). Key = the same cause name as in DeathCauses. Read by the Death causes tab.
        /// </summary>
        public readonly Dictionary<string, string> DeathCauseMostBy = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a ReportWindowData and moves it over the wire.
    ///
    /// Reply layout (all written with ZPackage):
    ///   int version, int status
    ///   status ok only, then:
    ///     string serverName, long startTicks, long endTicks, long nextDueTicks   (UTC ticks)
    ///     int joinedTotal, int killsTotal, int deathsTotal, int bossTotal, int hiddenPlayers
    ///     string topDamageName, double topDamage
    ///     int playerCount, then playerCount x { string name, bool joined, int mobKills, int deaths, double damage,
    ///                                           double highestHitDamage, string highestHitWeapon, string highestHitEnemy,
    ///                                           3 ranking tables: mobs killed, deaths by mob, death causes }
    ///     3 combined ranking tables (same three)
    ///     a ranking table is: int count, then count x { string name, int value }, at most MaxSnapshotRankEntries entries
    ///     int bossCount,   then bossCount   x { string boss, string killer, long timeTicks }
    ///   then, from this build on, an extension block (a reader of an older build stops before it and never sees it, a reader
    ///   of this build finds it missing in a reply from an older server and copes):
    ///     int extensionVersion (ReportProtocol.SnapshotExtensionVersion)
    ///     playerCount x { int bossesKilled }                                       (same order as the players above)
    ///     bossCount   x { int playersKilled, double highestHit, string hitBy, string hitWeapon }   (same order as the bosses above)
    ///   then a second extension block, only after the first one was written (a reader that finds the first block missing does not look for it):
    ///     int extensionVersion (ReportProtocol.SnapshotMobHitExtensionVersion)
    ///     playerCount x { int n, n x { string mob, double highestHit } }           (the mobs of that player's top five; same order as the players)
    ///     int n, n x { string mob, double highestHit, string hitBy }               (the mobs of the combined top five)
    ///   then a third extension block, only after the second one was read completely:
    ///     int extensionVersion (ReportProtocol.SnapshotKillerMobExtensionVersion)
    ///     int n, n x { string mob, string who }                                    (the mobs of the combined top five that killed players; who = the
    ///                                                                               player(s) who died to that mob most often, bosses included)
    ///   then a fourth extension block, only after the third one was read completely:
    ///     int extensionVersion (ReportProtocol.SnapshotDeathCauseExtensionVersion)
    ///     playerCount x { int n, n x { string cause, double damage, string type } } (the finishing hit of the causes in that player's top five; same order as the players)
    ///     int n, n x { string cause, double damage, string type }                  (the finishing hit of the causes in the combined top five)
    ///     int n, n x { string cause, string who }                                  (the combined top five: the player(s) who died of that cause most often)
    ///
    /// Reading never trusts the sender: unknown versions and absurd counts are rejected (null), strings are length-capped,
    /// and every number is clamped to something the window can show.
    /// </summary>
    internal static class ReportSnapshotCodec
    {
        private const int MaxReadRankEntries = 200;

        // ---------------------------------------------------------------- building (server side)

        /// <summary>
        /// The window data for right now, or just a status when the feature is switched off.
        /// Server side only (it reads ReportStats and the server name).
        /// </summary>
        internal static ReportWindowData BuildCurrent()
        {
            ReportWindowData data = new ReportWindowData();

            if (!ReportConfig.On)
            {
                data.Status = ReportProtocol.SnapshotStatusReportDisabled;
                return data;
            }

            if (!ReportConfig.WindowOn)
            {
                data.Status = ReportProtocol.SnapshotStatusWindowDisabled;
                return data;
            }

            ReportSnapshot snapshot = ReportStats.Snapshot();
            return FromSnapshot(snapshot, ServerRelay.GetServerDisplayName(), ReportScheduler.NextDueUtc(snapshot.StartUtc));
        }

        /// <summary>
        /// Turns a frozen copy of the statistics into window data: only players who took part, most kills first, capped.
        /// </summary>
        internal static ReportWindowData FromSnapshot(ReportSnapshot snapshot, string serverName, DateTime nextDueUtc)
        {
            ReportWindowData data = new ReportWindowData();
            data.Status = ReportProtocol.SnapshotStatusOk;
            data.ServerName = serverName ?? string.Empty;
            data.StartUtc = snapshot.StartUtc;
            data.EndUtc = snapshot.EndUtc;
            data.NextDueUtc = nextDueUtc;

            data.HasBossDetails = true;
            data.HasMobHitDetails = true;
            data.JoinedTotal = snapshot.JoinedCount;
            data.KillsTotal = snapshot.TotalMobKills;
            data.DeathsTotal = snapshot.TotalDeaths;
            data.BossTotal = snapshot.BossKills.Count;

            ReportPlayer top = snapshot.TopDamage();
            if (top != null)
            {
                data.TopDamageName = top.Name ?? string.Empty;
                data.TopDamage = top.Damage;
            }

            // A player is listed if they joined or did anything at all this period: killed a mob or a boss, died or dealt damage.
            // (The Discord table keeps its own, shorter rule.)
            List<ReportPlayer> rows = new List<ReportPlayer>();
            foreach (ReportPlayer player in snapshot.Players)
            {
                if (player.Joined || player.MobKills > 0 || player.Deaths > 0 || player.BossesKilled > 0 || player.Damage > 0.0)
                    rows.Add(player);
            }

            // Most kills first, then most deaths, then alphabetical (same order as the Discord table).
            rows.Sort(delegate (ReportPlayer a, ReportPlayer b)
            {
                int byKills = b.MobKills.CompareTo(a.MobKills);
                if (byKills != 0)
                    return byKills;

                int byDeaths = b.Deaths.CompareTo(a.Deaths);
                if (byDeaths != 0)
                    return byDeaths;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (rows.Count > ReportProtocol.MaxSnapshotPlayers)
            {
                data.HiddenPlayers = rows.Count - ReportProtocol.MaxSnapshotPlayers;
                rows.RemoveRange(ReportProtocol.MaxSnapshotPlayers, data.HiddenPlayers);
            }

            // The window gets its own copy of each row, with the ranking tables cut to the top entries (the snapshot itself is
            // left as it is).
            foreach (ReportPlayer player in rows)
            {
                ReportPlayer copy = new ReportPlayer
                {
                    Name = player.Name,
                    Joined = player.Joined,
                    MobKills = player.MobKills,
                    Deaths = player.Deaths,
                    Damage = player.Damage,
                    BossesKilled = player.BossesKilled,
                    HighestHitDamage = player.HighestHitDamage,
                    HighestHitWeapon = player.HighestHitWeapon,
                    HighestHitEnemy = player.HighestHitEnemy
                };
                CopyTop(player.MobKillsByType, copy.MobKillsByType);
                CopyTop(player.DeathsByMob, copy.DeathsByMob);
                CopyTop(player.DeathCauses, copy.DeathCauses);

                // The best hit on each of the mobs in the player's top five (every mob is tracked, only these are sent).
                foreach (string mob in copy.MobKillsByType.Keys)
                {
                    double best;
                    if (player.MobBestHits.TryGetValue(mob, out best) && best > 0.0)
                        copy.MobBestHits[mob] = best;
                }

                // The finishing hit of each of the causes in the player's top five (every cause is tracked, only these are sent).
                foreach (string cause in copy.DeathCauses.Keys)
                {
                    ReportDeathHit finishing;
                    if (player.DeathCauseHits.TryGetValue(cause, out finishing) && finishing.Damage > 0.0)
                        copy.DeathCauseHits[cause] = new ReportDeathHit { Damage = finishing.Damage, Type = finishing.Type ?? string.Empty };
                }

                data.Players.Add(copy);
            }

            // Combined rankings: summed over every player (also the ones left out of the list above), then cut.
            Dictionary<string, int> allMobKills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> allDeathsByMob = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> allDeathCauses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ReportPlayer player in snapshot.Players)
            {
                AddAll(allMobKills, player.MobKillsByType);
                AddAll(allDeathsByMob, player.DeathsByMob);
                AddAll(allDeathCauses, player.DeathCauses);
            }
            CopyTop(allMobKills, data.MobKillsByType);
            CopyTop(allDeathsByMob, data.DeathsByMob);
            CopyTop(allDeathCauses, data.DeathCauses);

            // Combined best hit per mob in the combined top five: the highest of every player's best hit on that kind of mob, and
            // who landed it (ties: the first player in the snapshot's order keeps it).
            foreach (string mob in data.MobKillsByType.Keys)
            {
                ReportMobHit best = null;
                foreach (ReportPlayer player in snapshot.Players)
                {
                    double damage;
                    if (!player.MobBestHits.TryGetValue(mob, out damage) || damage <= 0.0)
                        continue;

                    if (best == null || damage > best.Damage)
                        best = new ReportMobHit { Damage = damage, By = player.Name ?? string.Empty };
                }

                if (best != null)
                    data.MobBestHits[mob] = best;
            }

            // Death causes: the biggest finishing hit of each cause in the combined top five (the highest of every player's), and who
            // died of that cause most often. Worked out over EVERY player of the snapshot, not only the rows kept for the window.
            data.HasDeathCauseDetails = true;
            foreach (string cause in data.DeathCauses.Keys)
            {
                ReportDeathHit best = null;
                foreach (ReportPlayer player in snapshot.Players)
                {
                    ReportDeathHit finishing;
                    if (!player.DeathCauseHits.TryGetValue(cause, out finishing) || finishing.Damage <= 0.0)
                        continue;

                    if (best == null || finishing.Damage > best.Damage)
                        best = new ReportDeathHit { Damage = finishing.Damage, Type = finishing.Type ?? string.Empty };
                }

                if (best != null)
                    data.DeathCauseHits[cause] = best;

                string who = MostDeathsBy(snapshot.Players, cause, DeathCausesOf);
                if (who.Length > 0)
                    data.DeathCauseMostBy[cause] = who;
            }

            // Who died to each of the mobs in the combined "killed players" top five most often (bosses included, they are mobs in
            // DeathsByMob like any other). Worked out over EVERY player of the snapshot, not only the rows kept for the window.
            data.HasKillerMobDetails = true;
            foreach (string mob in data.DeathsByMob.Keys)
            {
                string who = MostDeathsBy(snapshot.Players, mob, DeathsByMobOf);
                if (who.Length > 0)
                    data.MobMostDeaths[mob] = who;
            }

            // The most recent boss kills, still in the order they happened.
            int skip = Math.Max(0, snapshot.BossKills.Count - ReportProtocol.MaxSnapshotBossKills);
            for (int i = skip; i < snapshot.BossKills.Count; i++)
                data.BossKills.Add(snapshot.BossKills[i]);

            return data;
        }

        /// <summary>
        /// The name of the player who died to <paramref name="mob"/> (a mob or a cause of death, whichever <paramref name="counts"/>
        /// picks from each player) the most times; empty when nobody did. Players tied for the
        /// most deaths are all named (alphabetically, at most ReportProtocol.MaxMostDeathsNames, then "+N more"), because with
        /// a few deaths a tie is the usual case and naming only one of them would be arbitrary.
        /// </summary>
        internal static string MostDeathsBy(List<ReportPlayer> players, string mob, Func<ReportPlayer, Dictionary<string, int>> counts)
        {
            int best = 0;
            List<string> names = new List<string>();
            foreach (ReportPlayer player in players)
            {
                int deaths;
                if (string.IsNullOrEmpty(player.Name) || !counts(player).TryGetValue(mob, out deaths) || deaths <= 0)
                    continue;

                if (deaths > best)
                {
                    best = deaths;
                    names.Clear();
                }

                if (deaths == best)
                    names.Add(player.Name);
            }

            if (names.Count == 0)
                return string.Empty;

            names.Sort(StringComparer.OrdinalIgnoreCase);

            int shown = Math.Min(names.Count, ReportProtocol.MaxMostDeathsNames);
            string text = string.Join(", ", names.GetRange(0, shown).ToArray());
            if (names.Count > shown)
                text += " +" + (names.Count - shown).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";

            return text.Length > ReportProtocol.MaxMostDeathsChars ? text.Substring(0, ReportProtocol.MaxMostDeathsChars) : text;
        }

        internal static Dictionary<string, int> DeathsByMobOf(ReportPlayer player)
        {
            return player.DeathsByMob;
        }

        internal static Dictionary<string, int> DeathCausesOf(ReportPlayer player)
        {
            return player.DeathCauses;
        }

        /// <summary>Adds every entry of <paramref name="from"/> to <paramref name="into"/>.</summary>
        private static void AddAll(Dictionary<string, int> into, Dictionary<string, int> from)
        {
            foreach (KeyValuePair<string, int> entry in from)
            {
                int n;
                into.TryGetValue(entry.Key, out n);
                into[entry.Key] = n + entry.Value;
            }
        }

        /// <summary>
        /// Copies the top ReportProtocol.MaxSnapshotRankEntries entries of <paramref name="from"/> into
        /// <paramref name="into"/>: highest count first, ties by name, the order the window ranks them in.
        /// </summary>
        private static void CopyTop(Dictionary<string, int> from, Dictionary<string, int> into)
        {
            if (from.Count == 0)
                return;

            List<KeyValuePair<string, int>> entries = new List<KeyValuePair<string, int>>(from);
            entries.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            int keep = Math.Min(ReportProtocol.MaxSnapshotRankEntries, entries.Count);
            for (int i = 0; i < keep; i++)
                into[entries[i].Key] = entries[i].Value;
        }

        // ---------------------------------------------------------------- writing (server side)

        internal static void Write(ZPackage package, ReportWindowData data)
        {
            package.Write(ReportProtocol.ProtocolVersion);
            package.Write(data.Status);

            if (data.Status != ReportProtocol.SnapshotStatusOk)
                return;

            package.Write(data.ServerName ?? string.Empty);
            package.Write(data.StartUtc.Ticks);
            package.Write(data.EndUtc.Ticks);
            package.Write(data.NextDueUtc.Ticks);

            package.Write(data.JoinedTotal);
            package.Write(data.KillsTotal);
            package.Write(data.DeathsTotal);
            package.Write(data.BossTotal);
            package.Write(data.HiddenPlayers);

            package.Write(data.TopDamageName ?? string.Empty);
            package.Write(data.TopDamage);

            package.Write(data.Players.Count);
            foreach (ReportPlayer player in data.Players)
            {
                package.Write(player.Name ?? string.Empty);
                package.Write(player.Joined);
                package.Write(player.MobKills);
                package.Write(player.Deaths);
                package.Write(player.Damage);
                package.Write(player.HighestHitDamage);
                package.Write(player.HighestHitWeapon ?? string.Empty);
                package.Write(player.HighestHitEnemy ?? string.Empty);
                WriteCounts(package, player.MobKillsByType);
                WriteCounts(package, player.DeathsByMob);
                WriteCounts(package, player.DeathCauses);
            }
            WriteCounts(package, data.MobKillsByType);
            WriteCounts(package, data.DeathsByMob);
            WriteCounts(package, data.DeathCauses);

            package.Write(data.BossKills.Count);
            foreach (ReportBossKill kill in data.BossKills)
            {
                package.Write(kill.Boss ?? string.Empty);
                package.Write(kill.Killer ?? string.Empty);
                package.Write(kill.TimeUtc.Ticks);
            }

            // Everything below is the extension block: older builds stop reading before it.
            package.Write(ReportProtocol.SnapshotExtensionVersion);
            foreach (ReportPlayer player in data.Players)
                package.Write(player.BossesKilled);

            foreach (ReportBossKill kill in data.BossKills)
            {
                package.Write(kill.PlayerKills);
                package.Write(kill.HighestHit);
                package.Write(kill.HighestHitBy ?? string.Empty);
                package.Write(kill.HighestHitWeapon ?? string.Empty);
            }

            // Second extension block: the best hit per kind of mob.
            package.Write(ReportProtocol.SnapshotMobHitExtensionVersion);
            foreach (ReportPlayer player in data.Players)
            {
                package.Write(player.MobBestHits.Count);
                foreach (KeyValuePair<string, double> hit in player.MobBestHits)
                {
                    package.Write(hit.Key ?? string.Empty);
                    package.Write(hit.Value);
                }
            }

            package.Write(data.MobBestHits.Count);
            foreach (KeyValuePair<string, ReportMobHit> hit in data.MobBestHits)
            {
                package.Write(hit.Key ?? string.Empty);
                package.Write(hit.Value.Damage);
                package.Write(hit.Value.By ?? string.Empty);
            }

            // Third extension block: who died to each of the top mobs most often.
            package.Write(ReportProtocol.SnapshotKillerMobExtensionVersion);
            package.Write(data.MobMostDeaths.Count);
            foreach (KeyValuePair<string, string> entry in data.MobMostDeaths)
            {
                package.Write(entry.Key ?? string.Empty);
                package.Write(entry.Value ?? string.Empty);
            }

            // Fourth extension block: the finishing hit of the top causes of death, and who died of each most often.
            package.Write(ReportProtocol.SnapshotDeathCauseExtensionVersion);
            foreach (ReportPlayer player in data.Players)
                WriteDeathHits(package, player.DeathCauseHits);

            WriteDeathHits(package, data.DeathCauseHits);

            package.Write(data.DeathCauseMostBy.Count);
            foreach (KeyValuePair<string, string> entry in data.DeathCauseMostBy)
            {
                package.Write(entry.Key ?? string.Empty);
                package.Write(entry.Value ?? string.Empty);
            }
        }

        private static void WriteDeathHits(ZPackage package, Dictionary<string, ReportDeathHit> hits)
        {
            package.Write(hits.Count);
            foreach (KeyValuePair<string, ReportDeathHit> hit in hits)
            {
                package.Write(hit.Key ?? string.Empty);
                package.Write(hit.Value.Damage);
                package.Write(hit.Value.Type ?? string.Empty);
            }
        }

        // ---------------------------------------------------------------- reading (client side)

        /// <summary>
        /// Reads a snapshot reply. Returns null when it cannot be understood (other protocol version, bad counts).
        /// </summary>
        internal static ReportWindowData Read(ZPackage package)
        {
            if (package.ReadInt() != ReportProtocol.ProtocolVersion)
                return null;

            ReportWindowData data = new ReportWindowData();
            data.Status = package.ReadInt();

            if (data.Status != ReportProtocol.SnapshotStatusOk)
                return data;

            data.ServerName = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxServerNameChars);
            data.StartUtc = TicksToUtc(package.ReadLong());
            data.EndUtc = TicksToUtc(package.ReadLong());
            data.NextDueUtc = TicksToUtc(package.ReadLong());

            data.JoinedTotal = ClampCount(package.ReadInt());
            data.KillsTotal = ClampCount(package.ReadInt());
            data.DeathsTotal = ClampCount(package.ReadInt());
            data.BossTotal = ClampCount(package.ReadInt());
            data.HiddenPlayers = ClampCount(package.ReadInt());

            data.TopDamageName = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
            data.TopDamage = ClampDamage(package.ReadDouble());

            int playerCount = package.ReadInt();
            if (playerCount < 0 || playerCount > ReportProtocol.MaxSnapshotPlayers)
                return null;

            for (int i = 0; i < playerCount; i++)
            {
                ReportPlayer player = new ReportPlayer();
                player.Name = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                player.Joined = package.ReadBool();
                player.MobKills = ClampCount(package.ReadInt());
                player.Deaths = ClampCount(package.ReadInt());
                player.Damage = ClampDamage(package.ReadDouble());
                player.HighestHitDamage = ClampDamage(package.ReadDouble());
                player.HighestHitWeapon = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                player.HighestHitEnemy = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                ReadCounts(package, player.MobKillsByType);
                ReadCounts(package, player.DeathsByMob);
                ReadCounts(package, player.DeathCauses);
                data.Players.Add(player);
            }
            ReadCounts(package, data.MobKillsByType);
            ReadCounts(package, data.DeathsByMob);
            ReadCounts(package, data.DeathCauses);

            int bossCount = package.ReadInt();
            if (bossCount < 0 || bossCount > ReportProtocol.MaxSnapshotBossKills)
                return null;

            for (int i = 0; i < bossCount; i++)
            {
                ReportBossKill kill = new ReportBossKill();
                kill.Boss = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                kill.Killer = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                kill.TimeUtc = TicksToUtc(package.ReadLong());
                data.BossKills.Add(kill);
            }

            // Each block follows the one before it: when one is missing (an older server) there is nothing to look for after it.
            if (ReadExtension(package, data) && ReadMobHitExtension(package, data) && ReadKillerMobExtension(package, data))
                ReadDeathCauseExtension(package, data);

            return data;
        }

        /// <summary>
        /// Reads the extension block after the boss list. A reply from a server of an older build ends before it: reading past
        /// the end of the package throws, which only means "nothing more to read" here. Nothing is applied unless the whole
        /// block was read, so a half-readable block never gives half-filled numbers.
        /// </summary>
        private static bool ReadExtension(ZPackage package, ReportWindowData data)
        {
            try
            {
                if (package.ReadInt() != ReportProtocol.SnapshotExtensionVersion)
                    return false;

                int[] bossesKilled = new int[data.Players.Count];
                for (int i = 0; i < bossesKilled.Length; i++)
                    bossesKilled[i] = ClampCount(package.ReadInt());

                int bossCount = data.BossKills.Count;
                int[] playersKilled = new int[bossCount];
                double[] highestHit = new double[bossCount];
                string[] hitBy = new string[bossCount];
                string[] hitWeapon = new string[bossCount];
                for (int i = 0; i < bossCount; i++)
                {
                    playersKilled[i] = ClampCount(package.ReadInt());
                    highestHit[i] = ClampDamage(package.ReadDouble());
                    hitBy[i] = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    hitWeapon[i] = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                }

                for (int i = 0; i < bossesKilled.Length; i++)
                    data.Players[i].BossesKilled = bossesKilled[i];

                for (int i = 0; i < bossCount; i++)
                {
                    ReportBossKill kill = data.BossKills[i];
                    kill.PlayerKills = playersKilled[i];
                    kill.HighestHit = highestHit[i];
                    kill.HighestHitBy = hitBy[i];
                    kill.HighestHitWeapon = hitWeapon[i];
                }

                data.HasBossDetails = true;
                return true;
            }
            catch (Exception)
            {
                RelayDiagnostics.Debug("[Report] The report reply has no per-boss numbers (the server is an older build).");
                return false;
            }
        }

        /// <summary>
        /// Reads the second extension block (the best hit per kind of mob). Same rules as ReadExtension: a reply without it
        /// simply ends here, and nothing is applied unless the whole block was read.
        /// </summary>
        private static bool ReadMobHitExtension(ZPackage package, ReportWindowData data)
        {
            try
            {
                if (package.ReadInt() != ReportProtocol.SnapshotMobHitExtensionVersion)
                    return false;

                Dictionary<string, double>[] perPlayer = new Dictionary<string, double>[data.Players.Count];
                for (int i = 0; i < perPlayer.Length; i++)
                {
                    perPlayer[i] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                    int count = package.ReadInt();
                    if (count < 0 || count > MaxReadRankEntries)
                        return false;

                    for (int j = 0; j < count; j++)
                    {
                        string mob = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                        double damage = ClampDamage(package.ReadDouble());
                        if (!string.IsNullOrWhiteSpace(mob) && damage > 0.0)
                            perPlayer[i][mob] = damage;
                    }
                }

                Dictionary<string, ReportMobHit> combined = new Dictionary<string, ReportMobHit>(StringComparer.OrdinalIgnoreCase);
                int combinedCount = package.ReadInt();
                if (combinedCount < 0 || combinedCount > MaxReadRankEntries)
                    return false;

                for (int j = 0; j < combinedCount; j++)
                {
                    string mob = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    double damage = ClampDamage(package.ReadDouble());
                    string by = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    if (!string.IsNullOrWhiteSpace(mob) && damage > 0.0)
                        combined[mob] = new ReportMobHit { Damage = damage, By = by ?? string.Empty };
                }

                for (int i = 0; i < perPlayer.Length; i++)
                {
                    foreach (KeyValuePair<string, double> hit in perPlayer[i])
                        data.Players[i].MobBestHits[hit.Key] = hit.Value;
                }

                foreach (KeyValuePair<string, ReportMobHit> hit in combined)
                    data.MobBestHits[hit.Key] = hit.Value;

                data.HasMobHitDetails = true;
                return true;
            }
            catch (Exception)
            {
                RelayDiagnostics.Debug("[Report] The report reply has no per-mob hits (the server is an older build).");
                return false;
            }
        }

        /// <summary>
        /// Reads the third extension block (who died to each of the top mobs most often). Same rules as the blocks before it: a
        /// reply without it simply ends here, and nothing is applied unless the whole block was read.
        /// </summary>
        private static bool ReadKillerMobExtension(ZPackage package, ReportWindowData data)
        {
            try
            {
                if (package.ReadInt() != ReportProtocol.SnapshotKillerMobExtensionVersion)
                    return false;

                int count = package.ReadInt();
                if (count < 0 || count > MaxReadRankEntries)
                    return false;

                Dictionary<string, string> mostDeaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string mob = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    string who = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxMostDeathsChars);
                    if (!string.IsNullOrWhiteSpace(mob) && !string.IsNullOrWhiteSpace(who))
                        mostDeaths[mob] = who;
                }

                foreach (KeyValuePair<string, string> entry in mostDeaths)
                    data.MobMostDeaths[entry.Key] = entry.Value;

                data.HasKillerMobDetails = true;
                return true;
            }
            catch (Exception)
            {
                RelayDiagnostics.Debug("[Report] The report reply has no \"most deaths by whom\" (the server is an older build).");
                return false;
            }
        }

        /// <summary>
        /// Reads the fourth extension block (the finishing hit of the top causes of death, and who died of each most often). Same
        /// rules as the blocks before it: a reply without it simply ends here, and nothing is applied unless the whole block was read.
        /// </summary>
        private static void ReadDeathCauseExtension(ZPackage package, ReportWindowData data)
        {
            try
            {
                if (package.ReadInt() != ReportProtocol.SnapshotDeathCauseExtensionVersion)
                    return;

                Dictionary<string, ReportDeathHit>[] perPlayer = new Dictionary<string, ReportDeathHit>[data.Players.Count];
                for (int i = 0; i < perPlayer.Length; i++)
                {
                    perPlayer[i] = ReadDeathHits(package);
                    if (perPlayer[i] == null)
                        return;
                }

                Dictionary<string, ReportDeathHit> combined = ReadDeathHits(package);
                if (combined == null)
                    return;

                int count = package.ReadInt();
                if (count < 0 || count > MaxReadRankEntries)
                    return;

                Dictionary<string, string> mostBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string cause = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                    string who = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxMostDeathsChars);
                    if (!string.IsNullOrWhiteSpace(cause) && !string.IsNullOrWhiteSpace(who))
                        mostBy[cause] = who;
                }

                for (int i = 0; i < perPlayer.Length; i++)
                {
                    foreach (KeyValuePair<string, ReportDeathHit> hit in perPlayer[i])
                        data.Players[i].DeathCauseHits[hit.Key] = hit.Value;
                }

                foreach (KeyValuePair<string, ReportDeathHit> hit in combined)
                    data.DeathCauseHits[hit.Key] = hit.Value;

                foreach (KeyValuePair<string, string> entry in mostBy)
                    data.DeathCauseMostBy[entry.Key] = entry.Value;

                data.HasDeathCauseDetails = true;
            }
            catch (Exception)
            {
                RelayDiagnostics.Debug("[Report] The report reply has no finishing hits (the server is an older build).");
            }
        }

        /// <summary>Reads one list of finishing hits; null when its count is not believable.</summary>
        private static Dictionary<string, ReportDeathHit> ReadDeathHits(ZPackage package)
        {
            int count = package.ReadInt();
            if (count < 0 || count > MaxReadRankEntries)
                return null;

            Dictionary<string, ReportDeathHit> hits = new Dictionary<string, ReportDeathHit>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string cause = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                double damage = ClampDamage(package.ReadDouble());
                string type = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars);
                if (!string.IsNullOrWhiteSpace(cause) && damage > 0.0)
                    hits[cause] = new ReportDeathHit { Damage = damage, Type = type ?? string.Empty };
            }

            return hits;
        }

        // ---------------------------------------------------------------- helpers


        private static void WriteCounts(ZPackage package, Dictionary<string, int> counts)
        {
            package.Write(counts.Count);
            foreach (KeyValuePair<string, int> entry in counts) { package.Write(entry.Key ?? string.Empty); package.Write(entry.Value); }
        }

        private static void ReadCounts(ZPackage package, Dictionary<string, int> counts)
        {
            int count = package.ReadInt();
            // A server of this build sends at most MaxSnapshotRankEntries; the reader allows far more so a reply from a build that
            // did not cut its tables is still understood (the window shows only the top entries), but still bounds the allocation.
            if (count < 0 || count > MaxReadRankEntries) throw new InvalidOperationException("Invalid mob ranking count.");
            for (int i = 0; i < count; i++) { string name = RelayProtocol.ReadSafeString(package, ReportProtocol.MaxNameChars); int value = ClampCount(package.ReadInt()); if (!string.IsNullOrWhiteSpace(name) && value > 0) counts[name] = value; }
        }

        private static DateTime TicksToUtc(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return DateTime.UtcNow;

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static int ClampCount(int value)
        {
            return Math.Max(0, value);
        }

        private static double ClampDamage(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                return 0.0;

            return value;
        }
    }
}
