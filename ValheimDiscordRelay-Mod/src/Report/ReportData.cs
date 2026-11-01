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
}
