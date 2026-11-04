using System;
using System.Collections.Generic;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Report;

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
