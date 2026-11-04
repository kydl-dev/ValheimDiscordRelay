using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimDiscordRelay.Report;

internal static partial class ReportEmbedBuilder
{
    // ---------------------------------------------------------------- tab 3: Mobs killed

    private static TabContent BuildMobsKilledTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Mobs killed",
            Color = ColorMobsKilled,
            Legend = "Highest hit received = the biggest single hit on that kind of mob"
        };

        List<KeyValuePair<string, int>> serverTop = TopFive(snapshot.MobKillsByType);
        if (serverTop.Count == 0)
        {
            tab.Note = "No mobs have been killed yet.";
            return tab;
        }

        // Server table: the highest hit of every mob is the best of all players, and By who names that player.
        List<string[]> serverCells = new List<string[]>();
        List<string> mobNames = new List<string>();
        List<string> whoNames = new List<string>();
        foreach (KeyValuePair<string, int> mob in serverTop)
        {
            double damage;
            string by;
            bool has = BestMobHit(snapshot, mob.Key, out damage, out by);

            mobNames.Add(mob.Key);
            whoNames.Add(has ? by : "-");
            serverCells.Add(new[] { mob.Key, Number(mob.Value), has ? Number(Math.Round(damage)) : "-", has && by.Length > 0 ? by : "-" });
        }

        Column[] serverColumns =
        {
            new Column("Name of mob", FitWidth(MinNameWidth, MaxNameWidth, mobNames), false),
            new Column("Amount", AmountWidth, true),
            new Column("Highest hit received", HitWidth, true),
            new Column("By who", FitWidth(6, MaxWhoWidth, whoNames), false)
        };
        AddTable(tab, "Top 5 most mobs killed on the server", serverColumns, serverCells);

        // One table per player who killed something: most kills first.
        int shown = 0;
        int omitted = 0;
        foreach (ReportPlayer player in snapshot.Players)
        {
            List<KeyValuePair<string, int>> top = TopFive(player.MobKillsByType);
            if (top.Count == 0)
                continue;

            if (shown >= MaxPlayerTablesPerTab)
            {
                omitted++;
                continue;
            }

            List<string[]> cells = new List<string[]>();
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, int> mob in top)
            {
                double hit;
                bool has = player.MobBestHits.TryGetValue(mob.Key, out hit) && hit > 0.0;
                names.Add(mob.Key);
                cells.Add(new[] { mob.Key, Number(mob.Value), has ? Number(Math.Round(hit)) : "-" });
            }

            Column[] columns =
            {
                new Column("Name of mob", FitWidth(MinNameWidth, MaxNameWidth, names), false),
                new Column("Amount", AmountWidth, true),
                new Column("Highest hit received", HitWidth, true)
            };
            AddTable(tab, "Top 5 most mobs killed by " + BossCatalog.EscapeDiscordMarkdown(player.Name), columns, cells);
            shown++;
        }

        AddOmittedNote(tab, omitted);
        return tab;
    }

    /// <summary>The biggest single hit any player landed on <paramref name="mob"/>, and who landed it (ties: the first player keeps it).</summary>
    private static bool BestMobHit(ReportSnapshot snapshot, string mob, out double damage, out string by)
    {
        damage = 0.0;
        by = string.Empty;
        bool found = false;

        foreach (ReportPlayer player in snapshot.Players)
        {
            double hit;
            if (!player.MobBestHits.TryGetValue(mob, out hit) || hit <= 0.0)
                continue;

            if (!found || hit > damage)
            {
                found = true;
                damage = hit;
                by = player.Name ?? string.Empty;
            }
        }

        return found;
    }

    // ---------------------------------------------------------------- tab 4: Killer mobs

    private static TabContent BuildKillerMobsTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Killer mobs",
            Color = ColorKillerMobs,
            Legend = "Most deaths by whom = who died to that mob most (bosses included)"
        };

        List<KeyValuePair<string, int>> serverTop = TopFive(snapshot.DeathsByMob);
        if (serverTop.Count == 0)
        {
            tab.Note = "No player has been killed by a mob yet.";
            return tab;
        }

        List<string[]> serverCells = new List<string[]>();
        List<string> mobNames = new List<string>();
        List<string> whoNames = new List<string>();
        foreach (KeyValuePair<string, int> mob in serverTop)
        {
            // Over every player, bosses included (they are mobs in DeathsByMob like any other).
            string who = ReportSnapshotCodec.MostDeathsBy(snapshot.Players, mob.Key, ReportSnapshotCodec.DeathsByMobOf);
            mobNames.Add(mob.Key);
            whoNames.Add(who.Length > 0 ? who : "-");
            serverCells.Add(new[] { mob.Key, Number(mob.Value), who.Length > 0 ? who : "-" });
        }

        Column[] serverColumns =
        {
            new Column("Name of mob", FitWidth(MinNameWidth, MaxNameWidth, mobNames), false),
            new Column("Amount", AmountWidth, true),
            new Column("Most deaths by whom", FitWidth(11, MaxWhoWidth, whoNames), false)
        };
        AddTable(tab, "Top 5 mobs that killed players on the server", serverColumns, serverCells);

        // One table per player who died to a mob: the players who died most first.
        List<ReportPlayer> players = new List<ReportPlayer>();
        foreach (ReportPlayer player in snapshot.Players)
        {
            if (player.DeathsByMob.Count > 0)
                players.Add(player);
        }
        SortByDeaths(players);

        int shown = 0;
        int omitted = 0;
        foreach (ReportPlayer player in players)
        {
            if (shown >= MaxPlayerTablesPerTab)
            {
                omitted++;
                continue;
            }

            List<string[]> cells = new List<string[]>();
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, int> mob in TopFive(player.DeathsByMob))
            {
                names.Add(mob.Key);
                cells.Add(new[] { mob.Key, Number(mob.Value) });
            }

            Column[] columns =
            {
                new Column("Name of mob", FitWidth(MinNameWidth, MaxNameWidth, names), false),
                new Column("Amount", AmountWidth, true)
            };
            AddTable(tab, "Top 5 mobs that killed " + BossCatalog.EscapeDiscordMarkdown(player.Name), columns, cells);
            shown++;
        }

        AddOmittedNote(tab, omitted);
        return tab;
    }

    // ---------------------------------------------------------------- tab 5: Death causes

    private static TabContent BuildDeathCausesTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Death causes",
            Color = ColorDeathCauses,
            Legend = "Finishing hit/damage = the hit that killed the player (the biggest one per cause)"
        };

        List<KeyValuePair<string, int>> serverTop = TopFive(snapshot.DeathCauses);
        if (serverTop.Count == 0)
        {
            tab.Note = "No player has died yet.";
            return tab;
        }

        List<string[]> serverCells = new List<string[]>();
        List<string> causeNames = new List<string>();
        List<string> hitTexts = new List<string>();
        List<string> whoNames = new List<string>();
        foreach (KeyValuePair<string, int> cause in serverTop)
        {
            // The biggest finishing hit of this cause over every player, and who died of it most often.
            ReportDeathHit best = null;
            foreach (ReportPlayer player in snapshot.Players)
            {
                ReportDeathHit finishing;
                if (!player.DeathCauseHits.TryGetValue(cause.Key, out finishing) || finishing.Damage <= 0.0)
                    continue;

                if (best == null || finishing.Damage > best.Damage)
                    best = finishing;
            }

            string who = ReportSnapshotCodec.MostDeathsBy(snapshot.Players, cause.Key, ReportSnapshotCodec.DeathCausesOf);
            string hit = FormatFinishingHit(best);

            causeNames.Add(cause.Key);
            hitTexts.Add(hit);
            whoNames.Add(who.Length > 0 ? who : "-");
            serverCells.Add(new[] { cause.Key, hit, Number(cause.Value), who.Length > 0 ? who : "-" });
        }

        Column[] serverColumns =
        {
            new Column("Cause name", FitWidth(10, 16, causeNames), false),
            new Column("Finishing hit/damage", FitWidth(10, 14, hitTexts), true),
            new Column("Amount", AmountWidth, true),
            new Column("Highest amount by who", FitWidth(9, 16, whoNames), false)
        };
        AddTable(tab, "Most common death causes on the server", serverColumns, serverCells);

        List<ReportPlayer> players = new List<ReportPlayer>();
        foreach (ReportPlayer player in snapshot.Players)
        {
            if (player.DeathCauses.Count > 0)
                players.Add(player);
        }
        SortByDeaths(players);

        int shown = 0;
        int omitted = 0;
        foreach (ReportPlayer player in players)
        {
            if (shown >= MaxPlayerTablesPerTab)
            {
                omitted++;
                continue;
            }

            List<string[]> cells = new List<string[]>();
            List<string> names = new List<string>();
            List<string> hits = new List<string>();
            foreach (KeyValuePair<string, int> cause in TopFive(player.DeathCauses))
            {
                ReportDeathHit finishing;
                player.DeathCauseHits.TryGetValue(cause.Key, out finishing);
                string hit = FormatFinishingHit(finishing);

                names.Add(cause.Key);
                hits.Add(hit);
                cells.Add(new[] { cause.Key, hit, Number(cause.Value) });
            }

            Column[] columns =
            {
                new Column("Cause name", FitWidth(10, 20, names), false),
                new Column("Finishing hit/damage", FitWidth(10, 14, hits), true),
                new Column("Amount", AmountWidth, true)
            };
            AddTable(tab, "Most common death causes by player " + BossCatalog.EscapeDiscordMarkdown(player.Name), columns, cells);
            shown++;
        }

        AddOmittedNote(tab, omitted);
        return tab;
    }

    /// <summary>"120 Blunt": the damage of the hit that killed the player and its type; "-" when it is not known.</summary>
    private static string FormatFinishingHit(ReportDeathHit hit)
    {
        if (hit == null || hit.Damage <= 0.0)
            return "-";

        return Number(Math.Round(hit.Damage)) + (string.IsNullOrEmpty(hit.Type) ? string.Empty : " " + hit.Type);
    }

    // ---------------------------------------------------------------- tab 6: Highest hits

    private static TabContent BuildHighestHitsTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Highest hits",
            Color = ColorHighestHits,
            Legend = "Each player's biggest single hit of the week"
        };

        List<ReportPlayer> rows = new List<ReportPlayer>();
        foreach (ReportPlayer player in snapshot.Players)
        {
            if (player.HighestHitDamage > 0.0)
                rows.Add(player);
        }

        if (rows.Count == 0)
        {
            tab.Note = "No single-hit damage recorded.";
            return tab;
        }

        rows.Sort(delegate (ReportPlayer a, ReportPlayer b)
        {
            int damage = b.HighestHitDamage.CompareTo(a.HighestHitDamage);
            return damage != 0 ? damage : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        // One line per player; as many lines per field as fit, the next field continues the list.
        StringBuilder value = new StringBuilder();
        bool firstField = true;

        foreach (ReportPlayer player in rows)
        {
            string line = "**" + BossCatalog.EscapeDiscordMarkdown(player.Name) + "** — " +
                          Number(Math.Round(player.HighestHitDamage)) +
                          " damage with " + BossCatalog.EscapeDiscordMarkdown(string.IsNullOrEmpty(player.HighestHitWeapon) ? "Unknown weapon" : player.HighestHitWeapon) +
                          " on " + BossCatalog.EscapeDiscordMarkdown(string.IsNullOrEmpty(player.HighestHitEnemy) ? "Unknown enemy" : player.HighestHitEnemy);

            if (line.Length > FieldValueBudget)
                line = line.Substring(0, FieldValueBudget - 1) + "…";

            if (value.Length > 0 && value.Length + 1 + line.Length > FieldValueBudget)
            {
                tab.Fields.Add(new EmbedField(firstField ? "Highest single hit — per player" : "Highest single hit — per player (cont.)", value.ToString(), false));
                firstField = false;
                value.Length = 0;
            }

            if (value.Length > 0)
                value.Append('\n');

            value.Append(line);
        }

        if (value.Length > 0)
            tab.Fields.Add(new EmbedField(firstField ? "Highest single hit — per player" : "Highest single hit — per player (cont.)", value.ToString(), false));

        return tab;
    }
}
