using System;
using System.Collections.Generic;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report;

/// <summary>The writing half of the codec (server side), split out so the class stays under the size limit.</summary>
internal static partial class ReportSnapshotCodec
{
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
}
