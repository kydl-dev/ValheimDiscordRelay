namespace ValheimDiscordRelay.Report;

/// <summary>What is known about one death of the local player, as handed to <see cref="ReportTracker.OnLocalPlayerDeath"/>.</summary>
internal sealed class DeathReport
{
    /// <summary>The local player's name.</summary>
    public string PlayerName { get; set; }

    /// <summary>The killer's prefab name, or null when there is no killer (environment).</summary>
    public string KillerPrefab { get; set; }

    /// <summary>The killer's Character.m_name (a localization key), or null; see EnglishNames.</summary>
    public string KillerNameKey { get; set; }

    /// <summary>The death message of the game, used to name an environmental cause.</summary>
    public string DeathText { get; set; }

    /// <summary>True when another player landed the killing blow (PvP). That is not a mob: the death is counted as a cause
    /// ("Killed by a player") and nothing is added to the mobs-that-killed-players ranking.</summary>
    public bool KilledByPlayer { get; set; }

    /// <summary>The killer's object key (BossAnnouncer.MakeKey) when a boss killed the player, so the server can count the
    /// death for that boss; null otherwise.</summary>
    public string KillerObjectKey { get; set; }

    /// <summary>The total damage of the hit that killed the player; 0 when unknown.</summary>
    public float FinishingDamage { get; set; }

    /// <summary>The damage type of that hit in English ("Slash", "Fire"...); null or empty when plain damage or unknown.</summary>
    public string FinishingType { get; set; }

    /// <summary>The English name of an environmental cause taken from the hit type ("Drowning", "Fall damage"...); null when a
    /// creature killed the player or the cause is unknown. Preferred over reading the death text.</summary>
    public string EnvironmentCause { get; set; }
}