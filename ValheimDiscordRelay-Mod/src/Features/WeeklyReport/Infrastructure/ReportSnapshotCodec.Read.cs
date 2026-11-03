using System;
using System.Collections.Generic;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report
{
    internal static partial class ReportSnapshotCodec
    {
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
    }
}
