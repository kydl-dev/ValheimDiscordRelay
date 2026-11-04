using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimDiscordRelay.Report;

internal static partial class ReportEmbedBuilder
{
    // ---------------------------------------------------------------- monospace tables

    /// <summary>
    /// Adds a table to the tab as one field (more when it does not fit in one; the header is repeated).
    /// </summary>
    private static void AddTable(TabContent tab, string title, Column[] columns, List<string[]> rows)
    {
        List<string> chunks = RenderTable(columns, rows);
        for (int i = 0; i < chunks.Count; i++)
            tab.Fields.Add(new EmbedField(i == 0 ? title : title + " (cont.)", chunks[i], false));
    }

    /// <summary>
    /// Draws a table in a code block. Every cell wraps inside its column (at spaces; a word longer than the column is split),
    /// and a row is as many lines high as its tallest cell. The header wraps the same way, and is underlined.
    /// </summary>
    /// <returns>One fenced block per field; several when the rows do not fit in one field.</returns>
    private static List<string> RenderTable(Column[] columns, List<string[]> rows)
    {
        List<string> chunks = new List<string>();

        int totalWidth = 2 * (columns.Length - 1);
        foreach (Column column in columns)
            totalWidth += column.Width;

        string header = RenderRow(columns, HeaderCells(columns)) + "\n" + new string('-', totalWidth);

        StringBuilder current = new StringBuilder(header);
        int rowsInCurrent = 0;

        foreach (string[] cells in rows)
        {
            string block = RenderRow(columns, cells);

            // 8 = the opening and closing code fences with their line breaks.
            if (rowsInCurrent > 0 && current.Length + 1 + block.Length + 8 > FieldValueBudget)
            {
                chunks.Add(Fence(current.ToString()));
                current.Length = 0;
                current.Append(header);
                rowsInCurrent = 0;
            }

            current.Append('\n').Append(block);
            rowsInCurrent++;
        }

        chunks.Add(Fence(current.ToString()));
        return chunks;
    }

    private static string[] HeaderCells(Column[] columns)
    {
        string[] cells = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++)
            cells[i] = columns[i].Header;

        return cells;
    }

    /// <summary>One table row: as many lines as its tallest (wrapped) cell needs, columns separated by two spaces.</summary>
    private static string RenderRow(Column[] columns, string[] cells)
    {
        List<string>[] wrapped = new List<string>[columns.Length];
        int height = 1;
        for (int c = 0; c < columns.Length; c++)
        {
            wrapped[c] = WrapCell(c < cells.Length ? cells[c] : string.Empty, columns[c].Width);
            height = Math.Max(height, wrapped[c].Count);
        }

        StringBuilder sb = new StringBuilder();
        for (int line = 0; line < height; line++)
        {
            if (line > 0)
                sb.Append('\n');

            StringBuilder text = new StringBuilder();
            for (int c = 0; c < columns.Length; c++)
            {
                if (c > 0)
                    text.Append("  ");

                string part = line < wrapped[c].Count ? wrapped[c][line] : string.Empty;
                text.Append(columns[c].Right ? part.PadLeft(columns[c].Width) : part.PadRight(columns[c].Width));
            }

            sb.Append(text.ToString().TrimEnd());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits a cell's text into lines of at most <paramref name="width"/> characters: at spaces where possible, inside a
    /// word only when the word alone is longer than the column. Nothing is ever dropped or replaced by "...".
    /// </summary>
    internal static List<string> WrapCell(string text, int width)
    {
        List<string> lines = new List<string>();
        width = Math.Max(1, width);

        string[] words = TableText(text).Split(' ');
        StringBuilder line = new StringBuilder();

        foreach (string raw in words)
        {
            string word = raw;
            if (word.Length == 0)
                continue;

            while (word.Length > width)
            {
                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Length = 0;
                }

                int cut = width;
                // Never split a surrogate pair (an emoji, say) in two.
                if (cut > 1 && char.IsHighSurrogate(word[cut - 1]))
                    cut--;

                lines.Add(word.Substring(0, cut));
                word = word.Substring(cut);
            }

            if (line.Length == 0)
            {
                line.Append(word);
            }
            else if (line.Length + 1 + word.Length <= width)
            {
                line.Append(' ').Append(word);
            }
            else
            {
                lines.Add(line.ToString());
                line.Length = 0;
                line.Append(word);
            }
        }

        if (line.Length > 0)
            lines.Add(line.ToString());

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    /// <summary>
    /// A column width that fits the longest text without wrapping when it can: at least <paramref name="min"/>, at most
    /// <paramref name="max"/> (longer texts then wrap).
    /// </summary>
    private static int FitWidth(int min, int max, List<string> texts)
    {
        int width = min;
        foreach (string text in texts)
            width = Math.Max(width, TableText(text).Length);

        return Math.Min(width, max);
    }

    /// <summary>
    /// Text made safe for a monospace table: no line breaks, no backtick that could close the code block, single spaces.
    /// </summary>
    private static string TableText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '`')
                sb.Append('\'');
            else if (c < ' ')
                sb.Append(' ');
            else
                sb.Append(c);
        }

        // "a  b" would make an empty word; the table is one space between words.
        return sb.ToString().Trim();
    }

    private static string Fence(string body)
    {
        return "```\n" + body + "\n```";
    }

    private static string Number(double value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string Number(int value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
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
