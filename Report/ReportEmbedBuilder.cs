using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Turns a ReportSnapshot into the webhook JSON (one embed). Pure formatting: it never touches the game, the config
    /// or the network, so it can be read and tested on its own.
    ///
    /// Layout:
    ///   title        Weekly report
    ///   description  **Server name** · 25 Sep – 2 Oct 2026
    ///   fields       Players joined | Bosses defeated | Highest damage dealt   (side by side)
    ///                Kills &amp; deaths: a monospace table with one row per player
    ///
    /// Discord limits respected: field value 1024 chars, 25 fields, 6000 chars for the whole embed.
    /// </summary>
    internal static class ReportEmbedBuilder
    {
        private const int EmbedColor = 0x3498DB;
        private const int FieldValueBudget = 1000;     // Discord allows 1024; keep a margin
        private const int MaxTableRows = 60;
        private const int MaxNameColumnWidth = 16;
        private const int MaxBossListChars = 700;

        /// <summary>
        /// Builds the complete webhook payload.
        /// </summary>
        /// <param name="snapshot">The statistics to report.</param>
        /// <param name="serverName">The server's display name (shown in bold).</param>
        /// <param name="isTest">True for the "Send Test Report" debug helper; the title says so.</param>
        internal static string Build(ReportSnapshot snapshot, string serverName, bool isTest)
        {
            StringBuilder sb = new StringBuilder(2048);

            sb.Append("{\"username\":\"").Append(Esc(ReportConfig.WebhookName)).Append('"');
            sb.Append(",\"allowed_mentions\":{\"parse\":[]}");
            sb.Append(",\"embeds\":[{");

            sb.Append("\"title\":\"").Append(Esc(isTest ? "Weekly report (test)" : "Weekly report")).Append('"');
            sb.Append(",\"description\":\"").Append(Esc(BuildDescription(snapshot, serverName))).Append('"');
            sb.Append(",\"color\":").Append(EmbedColor.ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"fields\":[");
            bool first = true;

            AppendField(sb, ref first, "Players joined",
                snapshot.JoinedCount.ToString(CultureInfo.InvariantCulture), true);

            AppendField(sb, ref first, "Bosses defeated", BuildBossesValue(snapshot), true);

            AppendField(sb, ref first, "Highest damage dealt", BuildTopDamageValue(snapshot), true);

            List<string> tableChunks = BuildTableChunks(snapshot);
            for (int i = 0; i < tableChunks.Count; i++)
            {
                AppendField(sb, ref first,
                    i == 0 ? "Kills & deaths" : "Kills & deaths (cont.)",
                    tableChunks[i], false);
            }

            sb.Append(']');

            sb.Append(",\"footer\":{\"text\":\"Mob kills and damage are counted for players running the mod.\"}");
            sb.Append(",\"timestamp\":\"")
              .Append(snapshot.EndUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
              .Append('"');

            sb.Append("}]}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- pieces

        private static string BuildDescription(ReportSnapshot snapshot, string serverName)
        {
            DateTime from = snapshot.StartUtc.ToLocalTime();
            DateTime to = snapshot.EndUtc.ToLocalTime();

            string range = from.ToString("d MMM", CultureInfo.InvariantCulture) + " \u2013 " +
                           to.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

            // The server name is bold, like in every other message that mentions it.
            return "**" + BossCatalog.EscapeDiscordMarkdown(serverName) + "** \u00B7 " + range;
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
                    names.Append(" \u00D7").Append(count.ToString(CultureInfo.InvariantCulture));
            }

            string list = names.ToString();
            if (list.Length > MaxBossListChars)
                list = list.Substring(0, MaxBossListChars - 1) + "\u2026";

            return "**" + total.ToString(CultureInfo.InvariantCulture) + "**\n" + list;
        }

        private static string BuildTopDamageValue(ReportSnapshot snapshot)
        {
            ReportPlayer top = snapshot.TopDamage();
            if (top == null)
                return "No damage recorded";

            return "**" + BossCatalog.EscapeDiscordMarkdown(top.Name) + "**\n" +
                   Math.Round(top.Damage).ToString("N0", CultureInfo.InvariantCulture) + " damage";
        }

        /// <summary>
        /// One row per player: name, mob kills, deaths. Split into several fields when it does not fit in one.
        /// </summary>
        private static List<string> BuildTableChunks(ReportSnapshot snapshot)
        {
            List<string> chunks = new List<string>();

            List<ReportPlayer> rows = new List<ReportPlayer>();
            foreach (ReportPlayer player in snapshot.Players)
            {
                if (player.Joined || player.MobKills > 0 || player.Deaths > 0)
                    rows.Add(player);
            }

            if (rows.Count == 0)
            {
                chunks.Add("No players were active this week.");
                return chunks;
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
            if (rows.Count > MaxTableRows)
            {
                hidden = rows.Count - MaxTableRows;
                rows.RemoveRange(MaxTableRows, hidden);
            }

            // Column width follows the longest name, within limits.
            const string nameHeader = "Player";
            int nameWidth = nameHeader.Length;
            foreach (ReportPlayer player in rows)
                nameWidth = Math.Max(nameWidth, TableName(player.Name).Length);
            nameWidth = Math.Min(nameWidth, MaxNameColumnWidth);

            string header = nameHeader.PadRight(nameWidth) + "  " + "Kills".PadLeft(5) + "  " + "Deaths".PadLeft(6);

            StringBuilder current = new StringBuilder();
            current.Append(header);

            foreach (ReportPlayer player in rows)
            {
                string name = TableName(player.Name);
                if (name.Length > nameWidth)
                    name = name.Substring(0, nameWidth - 1) + "\u2026";

                string line = name.PadRight(nameWidth) + "  " +
                              player.MobKills.ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  " +
                              player.Deaths.ToString(CultureInfo.InvariantCulture).PadLeft(6);

                // 8 = the opening and closing code fences with their line breaks.
                if (current.Length + 1 + line.Length + 8 > FieldValueBudget)
                {
                    chunks.Add(Fence(current.ToString()));
                    current.Length = 0;
                    current.Append(header);
                }

                current.Append('\n').Append(line);
            }

            string last = Fence(current.ToString());
            if (hidden > 0)
            {
                string more = "\n\u2026and " + hidden.ToString(CultureInfo.InvariantCulture) + " more";
                if (last.Length + more.Length <= FieldValueBudget + 24)
                    last += more;
            }

            chunks.Add(last);
            return chunks;
        }

        private static string Fence(string body)
        {
            return "```\n" + body + "\n```";
        }

        /// <summary>
        /// A player name made safe for a monospace table: no line breaks, and no backtick that could close the code block.
        /// </summary>
        private static string TableName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "?";

            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '`')
                    sb.Append('\'');
                else if (c < ' ')
                    sb.Append(' ');
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------- JSON helpers

        private static void AppendField(StringBuilder sb, ref bool first, string name, string value, bool inline)
        {
            if (!first)
                sb.Append(',');

            first = false;

            sb.Append("{\"name\":\"").Append(Esc(name)).Append('"');
            sb.Append(",\"value\":\"").Append(Esc(value)).Append('"');
            sb.Append(",\"inline\":").Append(inline ? "true" : "false").Append('}');
        }

        private static string Esc(string value)
        {
            return DiscordFormatting.JsonEscape(value);
        }
    }
}
