namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Wire constants for the weekly-report RPCs (game that owns a creature / dying player -> server).
    ///
    /// Activity RPC:  int version, int count, then count x { string player, float damage, int mobKills }.
    /// Event RPC:     int version, int kind, string key, string bossName, string killer.
    ///                kind EventPlayerDeath: the dying player is the sender (the server uses the sender's own character
    ///                name, the other strings are empty). kind EventBossKill: key identifies the boss object, bossName is its
    ///                display name, killer is the player who landed the killing blow.
    /// </summary>
    internal static class ReportProtocol
    {
        public const string ActivityRpcName = "ValheimDiscordRelay.ReportActivity.v1";
        public const string EventRpcName = "ValheimDiscordRelay.ReportEvent.v1";
        public const int ProtocolVersion = 1;

        public const int EventPlayerDeath = 1;
        public const int EventBossKill = 2;

        public const int MaxNameChars = 64;
        public const int MaxEntriesPerMessage = 64;
    }
}
