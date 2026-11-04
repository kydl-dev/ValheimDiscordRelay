using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimDiscordRelay.Report;

/// <summary>
/// Turns a ReportSnapshot into the webhook JSON of the weekly report: one Discord message (one embed) per tab of the
/// in-game report window, in the same order and with the same columns:
///
///   1. Players       summary (players joined, bosses defeated, highest damage) + a table: Player | Mobs killed | Bosses killed | Deaths | Total damage
///   2. Bosses        one block per boss kill (the same boss killed twice is two blocks): Killing blow, Highest hit received, Kills, When
///   3. Mobs killed   "Top 5 most mobs killed on the server" (Name of mob | Amount | Highest hit received | By who),
///                    then "Top 5 most mobs killed by X" for every player (Name of mob | Amount | Highest hit received)
///   4. Killer mobs   "Top 5 mobs that killed players on the server" (Name of mob | Amount | Most deaths by whom, bosses included),
///                    then "Top 5 mobs that killed X" for every player (Name of mob | Amount)
///   5. Death causes  "Most common death causes on the server" (Cause name | Finishing hit/damage | Amount | Highest amount by who),
///                    then "Most common death causes by player X" (Cause name | Finishing hit/damage | Amount)
///   6. Highest hits  each player's biggest single hit: damage, weapon and enemy
///
/// Everything is tracked in full by ReportStats; only the top 5 of every ranking is shown here.
///
/// Tables are monospace (a Discord code block). No cell is ever cut with "...": a cell that is wider than its column wraps
/// onto a second (third...) line of the same row, and a column is as wide as its longest text needs, up to a limit.
///
/// Discord limits respected: 1024 characters per field value, 25 fields and 6000 characters per embed. A tab that does not
/// fit in one message continues in the next one ("(2/3)"); nothing is dropped, except past MaxMessages in all.
///
/// Pure formatting: it never touches the game, the config (except the webhook name) or the network.
/// </summary>
internal static partial class ReportEmbedBuilder
{
    // Discord allows 1024 per field value and 6000 per embed; a margin is kept on both.
    private const int FieldValueBudget = 1000;
    private const int MaxEmbedChars = 5600;
    private const int MaxFields = 25;

    /// <summary>The most messages one report is split into. Only a server with very many players gets near it.</summary>
    private const int MaxMessages = 24;

    private const int TopCount = 5;
    private const int MaxPlayerTablesPerTab = 40;
    private const int MaxPlayerRows = 150;
    private const int MaxBossListChars = 700;

    // Column widths of the monospace tables, in characters.
    private const int MinNameWidth = 11;      // "Name of mob"
    private const int MaxNameWidth = 22;
    private const int AmountWidth = 6;
    private const int HitWidth = 11;          // "Highest hit"
    private const int MaxWhoWidth = 18;
    private const int PlayerNameMaxWidth = 18;

    private const long UnixEpochTicks = 621355968000000000L;

    private const int ColorPlayers = 0x3498DB;
    private const int ColorBosses = 0xE67E22;
    private const int ColorMobsKilled = 0x2ECC71;
    private const int ColorKillerMobs = 0xE74C3C;
    private const int ColorDeathCauses = 0x9B59B6;
    private const int ColorHighestHits = 0xF1C40F;

    private sealed class EmbedField
    {
        public string Name;
        public string Value;
        public bool Inline;

        public EmbedField(string name, string value, bool inline)
        {
            name = name.Replace('\r', ' ').Replace('\n', ' ');
            Name = name.Length > 250 ? name.Substring(0, 249) + "…" : name;
            // Safety net: Discord rejects the whole message when one value is over 1024 characters.
            Value = value != null && value.Length > 1024 ? value.Substring(0, 1023) + "…" : (value ?? string.Empty);
            Inline = inline;
        }
    }

    /// <summary>The content of one tab, before it is cut into messages.</summary>
    private sealed class TabContent
    {
        public string Name;
        public int Color;
        public string Legend;
        public string Note = string.Empty;   // shown under the description when there is nothing else to show
        public readonly List<EmbedField> Fields = new List<EmbedField>();
    }

    private sealed class Column
    {
        public string Header;
        public int Width;
        public bool Right;

        public Column(string header, int width, bool right)
        {
            Header = header;
            Width = width;
            Right = right;
        }
    }

    // ---------------------------------------------------------------- entry point

    /// <summary>
    /// Builds the complete webhook payloads of the report, in the order they are to be posted.
    /// </summary>
    /// <param name="snapshot">The statistics to report.</param>
    /// <param name="serverName">The server's display name (shown in bold).</param>
    /// <param name="isTest">True for the "Send Test Report" debug helper; the title says so.</param>
    /// <returns>One JSON body per Discord message. Never empty.</returns>
    internal static List<string> BuildMessages(ReportSnapshot snapshot, string serverName, bool isTest)
    {
        List<TabContent> tabs = new List<TabContent>();
        tabs.Add(BuildPlayersTab(snapshot));
        tabs.Add(BuildBossesTab(snapshot));
        tabs.Add(BuildMobsKilledTab(snapshot));
        tabs.Add(BuildKillerMobsTab(snapshot));
        tabs.Add(BuildDeathCausesTab(snapshot));
        tabs.Add(BuildHighestHitsTab(snapshot));

        string title = isTest ? "Weekly report (test)" : "Weekly report";
        string description = BuildDescription(snapshot, serverName);

        // Every tab is cut into pages first, so the total is known before anything is written.
        List<List<List<EmbedField>>> pagesOfTab = new List<List<List<EmbedField>>>();
        int totalPages = 0;
        foreach (TabContent tab in tabs)
        {
            List<List<EmbedField>> pages = Paginate(tab, title, description);
            pagesOfTab.Add(pages);
            totalPages += pages.Count;
        }

        List<string> messages = new List<string>();
        bool truncated = totalPages > MaxMessages;

        for (int t = 0; t < tabs.Count; t++)
        {
            List<List<EmbedField>> pages = pagesOfTab[t];
            for (int p = 0; p < pages.Count; p++)
            {
                if (messages.Count >= MaxMessages)
                    break;

                string footer = tabs[t].Legend;
                if (pages.Count > 1)
                    footer += " · Page " + (p + 1).ToString(CultureInfo.InvariantCulture) + "/" + pages.Count.ToString(CultureInfo.InvariantCulture);

                if (truncated && messages.Count == MaxMessages - 1)
                    footer += " · More pages not shown";

                string pageTitle = title + " — " + tabs[t].Name;
                if (pages.Count > 1)
                    pageTitle += " (" + (p + 1).ToString(CultureInfo.InvariantCulture) + "/" + pages.Count.ToString(CultureInfo.InvariantCulture) + ")";

                string pageDescription = description;
                if (tabs[t].Note.Length > 0 && pages[p].Count == 0)
                    pageDescription += "\n\n" + tabs[t].Note;

                messages.Add(BuildPayload(snapshot, pageTitle, pageDescription, tabs[t].Color, pages[p], footer));
            }
        }

        return messages;
    }

    // ---------------------------------------------------------------- Discord limits

    /// <summary>
    /// Cuts the fields of a tab into messages that each stay within Discord's limits (25 fields, the character budget).
    /// A tab always has at least one page, even when it has no fields.
    /// </summary>
    private static List<List<EmbedField>> Paginate(TabContent tab, string title, string description)
    {
        // Title (with " (n/m)"), description, footer (with the page text): what the fields share the budget with.
        int fixedChars = title.Length + tab.Name.Length + 20 + description.Length + tab.Legend.Length + 40;

        List<List<EmbedField>> pages = new List<List<EmbedField>>();
        List<EmbedField> current = new List<EmbedField>();
        int used = fixedChars;

        foreach (EmbedField field in tab.Fields)
        {
            int size = field.Name.Length + field.Value.Length;
            if (current.Count > 0 && (current.Count >= MaxFields || used + size > MaxEmbedChars))
            {
                pages.Add(current);
                current = new List<EmbedField>();
                used = fixedChars;
            }

            current.Add(field);
            used += size;
        }

        pages.Add(current);
        return pages;
    }

    private static string BuildPayload(ReportSnapshot snapshot, string title, string description, int color, List<EmbedField> fields, string footer)
    {
        StringBuilder sb = new StringBuilder(2048);

        sb.Append("{\"username\":\"").Append(Esc(ReportConfig.WebhookName)).Append('"');
        sb.Append(",\"allowed_mentions\":{\"parse\":[]}");
        sb.Append(",\"embeds\":[{");
        sb.Append("\"title\":\"").Append(Esc(title)).Append('"');
        sb.Append(",\"description\":\"").Append(Esc(description)).Append('"');
        sb.Append(",\"color\":").Append(color.ToString(CultureInfo.InvariantCulture));

        sb.Append(",\"fields\":[");
        bool first = true;
        foreach (EmbedField field in fields)
            AppendField(sb, ref first, field.Name, field.Value, field.Inline);
        sb.Append(']');

        sb.Append(",\"footer\":{\"text\":\"").Append(Esc(footer)).Append("\"}");
        sb.Append(",\"timestamp\":\"")
          .Append(snapshot.EndUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
          .Append('"');

        sb.Append("}]}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- tab 1: Players

    private static TabContent BuildPlayersTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Players",
            Color = ColorPlayers,
            Legend = "Mob kills and damage are counted for players running the mod"
        };

        tab.Fields.Add(new EmbedField("Players joined", snapshot.JoinedCount.ToString(CultureInfo.InvariantCulture), true));
        tab.Fields.Add(new EmbedField("Bosses defeated", BuildBossesValue(snapshot), true));
        tab.Fields.Add(new EmbedField("Highest damage dealt", BuildTopDamageValue(snapshot), true));

        // Every player who took part: joined, killed something, died or dealt damage (the same rule as the window).
        List<ReportPlayer> rows = new List<ReportPlayer>();
        foreach (ReportPlayer player in snapshot.Players)
        {
            if (player.Joined || player.MobKills > 0 || player.Deaths > 0 || player.BossesKilled > 0 || player.Damage > 0.0)
                rows.Add(player);
        }

        if (rows.Count == 0)
        {
            tab.Fields.Add(new EmbedField("Players", "No players were active this week.", false));
            return tab;
        }

        // Most kills first, then most deaths, then alphabetical.
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

        int hidden = 0;
        if (rows.Count > MaxPlayerRows)
        {
            hidden = rows.Count - MaxPlayerRows;
            rows.RemoveRange(MaxPlayerRows, hidden);
        }

        List<string> names = new List<string>();
        foreach (ReportPlayer player in rows)
            names.Add(player.Name);

        Column[] columns =
        {
            new Column("Player", FitWidth(6, PlayerNameMaxWidth, names), false),
            new Column("Mobs killed", 6, true),
            new Column("Bosses killed", 6, true),
            new Column("Deaths", 6, true),
            new Column("Total damage", 8, true)
        };

        List<string[]> cells = new List<string[]>();
        foreach (ReportPlayer player in rows)
        {
            cells.Add(new[]
            {
                player.Name,
                Number(player.MobKills),
                Number(player.BossesKilled),
                Number(player.Deaths),
                Number(Math.Round(player.Damage))
            });
        }

        AddTable(tab, "Players", columns, cells);

        if (hidden > 0)
            tab.Fields.Add(new EmbedField("…", "and " + Number(hidden) + " more players not shown", false));

        return tab;
    }

    // ---------------------------------------------------------------- tab 2: Bosses

    private static TabContent BuildBossesTab(ReportSnapshot snapshot)
    {
        TabContent tab = new TabContent
        {
            Name = "Bosses",
            Color = ColorBosses,
            Legend = "Kills = players that boss killed before it fell · Times are in your own time zone"
        };

        if (snapshot.BossKills.Count == 0)
        {
            tab.Note = "No bosses were defeated this week.";
            return tab;
        }

        // Newest first, like the window; the same boss killed twice is two entries.
        List<ReportBossKill> kills = new List<ReportBossKill>(snapshot.BossKills);
        kills.Sort(delegate (ReportBossKill a, ReportBossKill b) { return b.TimeUtc.CompareTo(a.TimeUtc); });

        foreach (ReportBossKill kill in kills)
        {
            string killer = string.IsNullOrEmpty(kill.Killer) ? "-" : BossCatalog.EscapeDiscordMarkdown(kill.Killer);
            bool details = kill.HighestHit > 0.0;

            StringBuilder value = new StringBuilder();
            value.Append("**Killing blow:** ").Append(killer).Append('\n');
            value.Append("**Highest hit received:** ").Append(FormatBossHit(kill)).Append('\n');
            value.Append("**Kills:** ").Append(details || kill.PlayerKills > 0 ? Number(kill.PlayerKills) : "-").Append('\n');
            value.Append("**When:** ").Append(FormatWhen(kill.TimeUtc));

            tab.Fields.Add(new EmbedField(BossCatalog.EscapeDiscordMarkdown(string.IsNullOrEmpty(kill.Boss) ? "Unknown boss" : kill.Boss),
                value.ToString(), false));
        }

        return tab;
    }

    /// <summary>"4,210 - Astrid (Iron sword)": the damage, then who landed the hit and with what. "-" when it is not known.</summary>
    private static string FormatBossHit(ReportBossKill kill)
    {
        if (kill.HighestHit <= 0.0)
            return "-";

        string text = Number(Math.Round(kill.HighestHit));
        bool hasPlayer = !string.IsNullOrEmpty(kill.HighestHitBy);
        bool hasWeapon = !string.IsNullOrEmpty(kill.HighestHitWeapon);

        if (hasPlayer && hasWeapon)
            return text + " — " + BossCatalog.EscapeDiscordMarkdown(kill.HighestHitBy) + " (" + BossCatalog.EscapeDiscordMarkdown(kill.HighestHitWeapon) + ")";

        if (hasPlayer)
            return text + " — " + BossCatalog.EscapeDiscordMarkdown(kill.HighestHitBy);

        if (hasWeapon)
            return text + " (" + BossCatalog.EscapeDiscordMarkdown(kill.HighestHitWeapon) + ")";

        return text;
    }

    /// <summary>A Discord timestamp: every reader sees it in their own time zone.</summary>
    private static string FormatWhen(DateTime timeUtc)
    {
        long seconds = (timeUtc.Ticks - UnixEpochTicks) / TimeSpan.TicksPerSecond;
        return "<t:" + seconds.ToString(CultureInfo.InvariantCulture) + ":f>";
    }

    // ---------------------------------------------------------------- summary pieces (Players tab)

    private static string BuildDescription(ReportSnapshot snapshot, string serverName)
    {
        DateTime from = snapshot.StartUtc.ToLocalTime();
        DateTime to = snapshot.EndUtc.ToLocalTime();

        string range = from.ToString("d MMM", CultureInfo.InvariantCulture) + " – " +
                       to.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        // The server name is bold, like in every other message that mentions it.
        return "**" + BossCatalog.EscapeDiscordMarkdown(serverName) + "** · " + range;
    }

    private static string BuildBossesValue(ReportSnapshot snapshot)
    {
        int total = snapshot.BossKills.Count;
        if (total == 0)
            return "0";

        // Eikthyr, The Elder x2 ... in the order the bosses were first killed.
        List<string> order = new List<string>();
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (ReportBossKill kill in snapshot.BossKills)
        {
            int existing;
            if (counts.TryGetValue(kill.Boss, out existing))
            {
                counts[kill.Boss] = existing + 1;
            }
            else
            {
                counts[kill.Boss] = 1;
                order.Add(kill.Boss);
            }
        }

        StringBuilder names = new StringBuilder();
        foreach (string boss in order)
        {
            if (names.Length > 0)
                names.Append(", ");

            names.Append(BossCatalog.EscapeDiscordMarkdown(boss));

            int count = counts[boss];
            if (count > 1)
                names.Append(" ×").Append(count.ToString(CultureInfo.InvariantCulture));
        }

        string list = names.ToString();
        if (list.Length > MaxBossListChars)
            list = list.Substring(0, MaxBossListChars - 1) + "…";

        return "**" + total.ToString(CultureInfo.InvariantCulture) + "**\n" + list;
    }

    private static string BuildTopDamageValue(ReportSnapshot snapshot)
    {
        ReportPlayer top = snapshot.TopDamage();
        if (top == null)
            return "No damage recorded";

        return "**" + BossCatalog.EscapeDiscordMarkdown(top.Name) + "**\n" +
               Number(Math.Round(top.Damage)) + " damage";
    }

    // ---------------------------------------------------------------- rankings

    /// <summary>The five biggest entries: highest count first, ties alphabetical (the same order as the window).</summary>
    private static List<KeyValuePair<string, int>> TopFive(Dictionary<string, int> counts)
    {
        List<KeyValuePair<string, int>> rows = new List<KeyValuePair<string, int>>(counts);
        rows.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            int byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        if (rows.Count > TopCount)
            rows.RemoveRange(TopCount, rows.Count - TopCount);

        return rows;
    }

    /// <summary>The players who died most first, ties alphabetical (the order of the window).</summary>
    private static void SortByDeaths(List<ReportPlayer> players)
    {
        players.Sort(delegate (ReportPlayer a, ReportPlayer b)
        {
            int byDeaths = b.Deaths.CompareTo(a.Deaths);
            return byDeaths != 0 ? byDeaths : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void AddOmittedNote(TabContent tab, int omitted)
    {
        if (omitted > 0)
            tab.Fields.Add(new EmbedField("…", "and " + Number(omitted) + " more " + (omitted == 1 ? "player" : "players") + " not shown", false));
    }

}
