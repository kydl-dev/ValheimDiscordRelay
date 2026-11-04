namespace ValheimDiscordRelay.Report;

/// <summary>
/// Wire constants for the weekly-report RPCs.
///
/// Activity RPC:  int version, int count, then count x { string player, float damage, int mobKills }.
/// Event RPC:     int version, int kind, string key, string bossName, string killer   (+ float damage for EventHighestHit).
///                kind EventPlayerDeath: the dying player is the sender (the server uses the sender's own character
///                name). key is the killer's prefab name (empty when there is none), bossName is the cause of death,
///                killer is the killer's object key (BossAnnouncer.MakeKey) when a boss killed the player, otherwise
///                empty; the server counts the death for that boss (older builds sent it empty and ignore it).
///                kind EventBossKill: key identifies the boss object, bossName is its display name, killer is the player
///                who landed the killing blow.
///                kind EventMobKill: key is the mob's prefab name, bossName is its name (English text, or the name key
///                for the server to turn into English; empty from older builds), killer is the player who killed it.
///                kind EventBossHit: the best hit a boss received, as measured by the game that owned it. key is the boss's
///                instance key (BossCatalog.InstanceKey), bossName is the weapon, killer is the player who landed the hit,
///                damage is the health the hit removed. Same layout as EventHighestHit. A server of an older build ignores it.
///                kind EventMobHit: the best hit one player landed on one kind of mob (never a boss), as measured by the game that
///                owned the mob. key is the mob's prefab name, bossName is its name (English text, or the name key for the server
///                to turn into English), killer is the player who landed the hit, damage is the health the hit removed. Same
///                layout as EventHighestHit. A server of an older build logs it as an unknown kind and ignores it.
///                kind EventDeathHit: the finishing hit (the hit that killed the player) of the death the same game reported just before
///                with EventPlayerDeath. The dying player is the sender, like for EventPlayerDeath. key is the damage type of the hit
///                (English, may be empty), bossName is the cause of death exactly as EventPlayerDeath sent it, killer is the killer's prefab name
///                (empty for an environmental death; an older game sends it empty always), damage is the total damage of the hit. A server of an older build logs it as an unknown kind and ignores it.
///                kind EventHighestHit: key is the weapon, bossName is the enemy (both English or keys, see
///                EnglishNames), killer is the player who landed the hit, damage is the health the hit removed.
///                Creature and weapon names are never trusted to be English: the server runs them through EnglishNames.
///
/// Period reset:  int version                                       (server -> every player's game)
///                Sent when the server starts a new statistics period, so a game that remembers its players' highest hits
///                (to avoid re-sending them, see ReportTracker) forgets them.
///
/// In-game report window (see ReportWindow):
/// Snapshot request:  int version                                   (player's game -> server)
/// Snapshot reply:    see ReportSnapshotCodec for the layout        (server -> that player's game). Newer fields are
///                    appended after the original layout, so a game of an older build still reads the reply.
/// </summary>
internal static class ReportProtocol
{
    public const string ActivityRpcName = "ValheimDiscordRelay.ReportActivity.v1";
    public const string EventRpcName = "ValheimDiscordRelay.ReportEvent.v1";
    public const string SnapshotRequestRpcName = "ValheimDiscordRelay.ReportSnapshotRequest.v1";
    public const string SnapshotReplyRpcName = "ValheimDiscordRelay.ReportSnapshotReply.v1";
    public const string PeriodResetRpcName = "ValheimDiscordRelay.ReportPeriodReset.v1";
    public const int ProtocolVersion = 3;

    public const int EventPlayerDeath = 1;
    public const int EventBossKill = 2;
    public const int EventMobKill = 3;
    public const int EventHighestHit = 4;
    public const int EventBossHit = 5;
    public const int EventMobHit = 6;
    public const int EventDeathHit = 7;

    // Snapshot reply status. Anything other than Ok carries no numbers.
    public const int SnapshotStatusOk = 0;
    public const int SnapshotStatusReportDisabled = 1;
    public const int SnapshotStatusWindowDisabled = 2;

    /// <summary>Version of the block of extra fields at the end of a snapshot reply (see ReportSnapshotCodec).</summary>
    public const int SnapshotExtensionVersion = 1;

    /// <summary>Version of the second extension block, right after the first one: the best hit per kind of mob (see ReportSnapshotCodec).</summary>
    public const int SnapshotMobHitExtensionVersion = 2;

    /// <summary>Version of the third extension block, right after the second one: who died to each of the top mobs most often (see ReportSnapshotCodec).</summary>
    public const int SnapshotKillerMobExtensionVersion = 3;

    /// <summary>Version of the fourth extension block, right after the third one: the finishing hit of each cause of death and who died of it most often (see ReportSnapshotCodec).</summary>
    public const int SnapshotDeathCauseExtensionVersion = 4;

    /// <summary>Longest "who died most" text of one mob: up to three tied player names plus "+N more".</summary>
    public const int MaxMostDeathsChars = 256;

    /// <summary>How many tied players the "Most deaths by whom" cell names before it says "+N more".</summary>
    public const int MaxMostDeathsNames = 3;

    public const int MaxNameChars = 64;
    public const int MaxServerNameChars = 128;
    public const int MaxEntriesPerMessage = 64;

    // Caps for one snapshot reply. The totals in the reply are always complete. The player and boss lists are capped, and
    // every ranking table (mobs killed, mobs that killed players, causes of death; combined and per player) carries only
    // the top MaxSnapshotRankEntries entries, because that is all the window shows. A typical reply is a few kilobytes;
    // the worst case (100 players, every name at its length limit) is roughly 100 KB.
    public const int MaxSnapshotPlayers = 100;
    public const int MaxSnapshotBossKills = 50;
    public const int MaxSnapshotRankEntries = 5;
}
