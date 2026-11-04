using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Report;

public sealed partial class ReportWindow
{
    /// <summary>
    /// The Mobs killed tab: "Top 5 most mobs killed on the server" first, then one "Top 5 most mobs killed by <player>" table
    /// per player who killed something. Columns: Name of mob | Amount | Highest hit received, and for the server table also
    /// By who. Highest hit received is the biggest single hit (health removed) landed on that kind of mob: by that player in
    /// a player's table, by anybody in the server table (and "By who" says who). Every cell wraps instead of being cut.
    /// </summary>
    private bool BuildMobsKilled()
    {
        VisualElement content = _scroll.Content;
        bool details = _data.HasMobHitDetails;

        AddMobsKilledTable(content, "Top 5 most mobs killed on the server", GetTopFive(_data.MobKillsByType), _data.MobBestHits, true, details);

        foreach (ReportPlayer player in _killPlayers)
        {
            // The player's own best hits, in the same shape as the server's (the player is the one who landed every one of them).
            Dictionary<string, ReportMobHit> hits = new Dictionary<string, ReportMobHit>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> hit in player.MobBestHits)
                hits[hit.Key] = new ReportMobHit { Damage = hit.Value, By = player.Name };

            string title = "Top 5 most mobs killed by " + player.Name + (IsYou(player.Name) ? "  (you)" : string.Empty);
            AddMobsKilledTable(content, title, GetTopFive(player.MobKillsByType), hits, false, details);
        }

        return true;
    }

    private void AddMobsKilledTable(VisualElement content, string title, KeyValuePair<string, int>[] top,
        Dictionary<string, ReportMobHit> hits, bool serverTable, bool details)
    {
        // The title wraps (a player's name can be long); it is at least one line high.
        Label heading = MakeHeading(title);
        heading.style.whiteSpace = WhiteSpace.Normal;
        heading.style.textOverflow = TextOverflow.Clip;
        heading.style.minHeight = TableHeadingHeight;
        content.Add(heading);

        // The name column is never narrower than its longest name needs, so a name is not squeezed into a column of its own
        // too narrow for it; a name longer than the cap wraps onto a second line.
        float nameMinWidth = MobsNameMinWidthFloor;
        for (int i = 0; i < top.Length; i++)
            nameMinWidth = Mathf.Max(nameMinWidth, EstimateTextWidth(top[i].Key));
        nameMinWidth = Mathf.Min(nameMinWidth, MobsNameMinWidthCap);

        VisualElement head = MakeFlexRow("vdr-table-header");
        head.style.minHeight = HeaderHeight;
        head.Add(WrappedNameCell("Name of mob", "vdr-head-cell", null, nameMinWidth));
        head.Add(WrappedFixedCell("Amount", "vdr-head-cell", true, MobsAmountWidth, null));
        head.Add(WrappedFixedCell("Highest hit received", "vdr-head-cell", true, MobsHitWidth, null));
        if (serverTable)
            head.Add(WrappedFixedCell("By who", "vdr-head-cell", false, MobsByWhoWidth, null));
        content.Add(head);

        if (top.Length == 0)
        {
            VisualElement none = MakeFlexRow(null);
            none.Add(WrappedCell("No mobs have been killed yet.", "vdr-cell", null));
            content.Add(none);
        }

        for (int i = 0; i < top.Length; i++)
        {
            VisualElement row = MakeFlexRow((i & 1) == 0 ? "vdr-row-alt" : null);

            ReportMobHit hit = null;
            bool hasHit = details && hits != null && hits.TryGetValue(top[i].Key, out hit) && hit != null && hit.Damage > 0.0;

            string hitText = hasHit ? Math.Round(hit.Damage).ToString("N0", Inv) : "-";

            row.Add(WrappedNameCell(top[i].Key, "vdr-cell", null, nameMinWidth));
            row.Add(WrappedFixedCell(top[i].Value.ToString("N0", Inv), "vdr-cell", true, MobsAmountWidth, null));
            row.Add(WrappedFixedCell(hitText, "vdr-cell", true, MobsHitWidth, null));

            if (serverTable)
            {
                bool hasWho = hasHit && !string.IsNullOrEmpty(hit.By);
                row.Add(WrappedFixedCell(hasWho ? hit.By : "-", "vdr-cell", false, MobsByWhoWidth, hasWho && IsYou(hit.By) ? "vdr-you" : null));
            }

            content.Add(row);
        }

        content.Add(MakeBlock(TableGap));
    }

    /// <summary>How much room the Mobs killed tab needs: one table for the server and one per player, each with at least one row.</summary>
    private float MobsKilledContentHeight()
    {
        float height = MobsTableHeight(GetTopFive(_data.MobKillsByType).Length);
        foreach (ReportPlayer player in _killPlayers)
            height += MobsTableHeight(GetTopFive(player.MobKillsByType).Length);

        return height;
    }

    private static float MobsTableHeight(int rows)
    {
        return TableHeadingHeight + HeaderHeight + Mathf.Max(1, rows) * RowHeight + TableGap;
    }

    private static float EstimateTextWidth(string text)
    {
        return (text == null ? 0 : text.Length) * EstimatedCharWidth + EstimatedCellPadding;
    }

    /// <summary>
    /// The Killer mobs tab: "Top 5 mobs that killed players on the server" first, then one "Top 5 mobs that killed <player>"
    /// table per player who died to a mob. Columns: Name of mob | Amount, and for the server table also Most deaths by whom: the
    /// player who died to that mob the most times (bosses are included). Cells wrap instead of being cut with "...", and the
    /// name column is never narrower than its longest name needs (see AddMobsKilledTable).
    /// </summary>
    private bool BuildKillerMobs()
    {
        VisualElement content = _scroll.Content;

        AddKillerMobsTable(content, "Top 5 mobs that killed players on the server", GetTopFive(_data.DeathsByMob), true);

        foreach (ReportPlayer player in _killerPlayers)
        {
            string title = "Top 5 mobs that killed " + player.Name + (IsYou(player.Name) ? "  (you)" : string.Empty);
            AddKillerMobsTable(content, title, GetTopFive(player.DeathsByMob), false);
        }

        return true;
    }

    private void AddKillerMobsTable(VisualElement content, string title, KeyValuePair<string, int>[] top, bool serverTable)
    {
        // The title wraps (a player's name can be long); it is at least one line high.
        Label heading = MakeHeading(title);
        heading.style.whiteSpace = WhiteSpace.Normal;
        heading.style.textOverflow = TextOverflow.Clip;
        heading.style.minHeight = TableHeadingHeight;
        content.Add(heading);

        float nameMinWidth = MobsNameMinWidthFloor;
        for (int i = 0; i < top.Length; i++)
            nameMinWidth = Mathf.Max(nameMinWidth, EstimateTextWidth(top[i].Key));
        nameMinWidth = Mathf.Min(nameMinWidth, MobsNameMinWidthCap);

        VisualElement head = MakeFlexRow("vdr-table-header");
        head.style.minHeight = HeaderHeight;
        head.Add(WrappedNameCell("Name of mob", "vdr-head-cell", null, nameMinWidth));
        head.Add(WrappedFixedCell("Amount", "vdr-head-cell", true, MobsAmountWidth, null));
        if (serverTable)
            head.Add(WrappedFixedCell("Most deaths by whom", "vdr-head-cell", false, MobsMostDeathsWidth, null));
        content.Add(head);

        if (top.Length == 0)
        {
            VisualElement none = MakeFlexRow(null);
            none.Add(WrappedCell("No player has been killed by a mob yet.", "vdr-cell", null));
            content.Add(none);
        }

        bool details = _data != null && _data.HasKillerMobDetails;

        for (int i = 0; i < top.Length; i++)
        {
            VisualElement row = MakeFlexRow((i & 1) == 0 ? "vdr-row-alt" : null);

            row.Add(WrappedNameCell(top[i].Key, "vdr-cell", null, nameMinWidth));
            row.Add(WrappedFixedCell(top[i].Value.ToString("N0", Inv), "vdr-cell", true, MobsAmountWidth, null));

            if (serverTable)
            {
                string who = null;
                bool hasWho = details && _data.MobMostDeaths.TryGetValue(top[i].Key, out who) && !string.IsNullOrEmpty(who);
                row.Add(WrappedFixedCell(hasWho ? who : "-", "vdr-cell", false, MobsMostDeathsWidth, hasWho && IsYou(who) ? "vdr-you" : null));
            }

            content.Add(row);
        }

        content.Add(MakeBlock(TableGap));
    }

    /// <summary>How much room the Killer mobs tab needs: one table for the server and one per player, each with at least one row.</summary>
    private float KillerMobsContentHeight()
    {
        float height = MobsTableHeight(GetTopFive(_data.DeathsByMob).Length);
        foreach (ReportPlayer player in _killerPlayers)
            height += MobsTableHeight(GetTopFive(player.DeathsByMob).Length);

        return height;
    }

    /// <summary>
    /// The Death causes tab: "Most common death causes on the server" first, then one "Most common death causes by player X"
    /// table per player who died. Columns: Cause name | Finishing hit/damage | Amount, and for the server table also Highest
    /// amount by who: the player who died of that cause the most times (the amount is the total over all players). Cells wrap
    /// instead of being cut with "...", and the name column is never narrower than its longest name needs.
    /// </summary>
    private bool BuildDeathCauses()
    {
        VisualElement content = _scroll.Content;

        AddDeathCausesTable(content, "Most common death causes on the server", GetTopFive(_data.DeathCauses), _data.DeathCauseHits, true);

        foreach (ReportPlayer player in _deathPlayers)
        {
            string title = "Most common death causes by player " + player.Name + (IsYou(player.Name) ? "  (you)" : string.Empty);
            AddDeathCausesTable(content, title, GetTopFive(player.DeathCauses), player.DeathCauseHits, false);
        }

        return true;
    }

    private void AddDeathCausesTable(VisualElement content, string title, KeyValuePair<string, int>[] top,
        Dictionary<string, ReportDeathHit> hits, bool serverTable)
    {
        // The title wraps (a player's name can be long); it is at least one line high.
        Label heading = MakeHeading(title);
        heading.style.whiteSpace = WhiteSpace.Normal;
        heading.style.textOverflow = TextOverflow.Clip;
        heading.style.minHeight = TableHeadingHeight;
        content.Add(heading);

        float nameMinWidth = MobsNameMinWidthFloor;
        for (int i = 0; i < top.Length; i++)
            nameMinWidth = Mathf.Max(nameMinWidth, EstimateTextWidth(top[i].Key));
        nameMinWidth = Mathf.Min(nameMinWidth, MobsNameMinWidthCap);

        VisualElement head = MakeFlexRow("vdr-table-header");
        head.style.minHeight = HeaderHeight;
        head.Add(WrappedNameCell("Cause name", "vdr-head-cell", null, nameMinWidth));
        head.Add(WrappedFixedCell("Finishing hit/damage", "vdr-head-cell", true, DeathHitWidth, null));
        head.Add(WrappedFixedCell("Amount", "vdr-head-cell", true, MobsAmountWidth, null));
        if (serverTable)
            head.Add(WrappedFixedCell("Highest amount by who", "vdr-head-cell", false, DeathByWhoWidth, null));
        content.Add(head);

        if (top.Length == 0)
        {
            VisualElement none = MakeFlexRow(null);
            none.Add(WrappedCell("No player has died yet.", "vdr-cell", null));
            content.Add(none);
        }

        bool details = _data != null && _data.HasDeathCauseDetails;

        for (int i = 0; i < top.Length; i++)
        {
            VisualElement row = MakeFlexRow((i & 1) == 0 ? "vdr-row-alt" : null);

            ReportDeathHit hit = null;
            bool hasHit = details && hits != null && hits.TryGetValue(top[i].Key, out hit) && hit != null && hit.Damage > 0.0;
            string hitText = !hasHit ? "-" : Math.Round(hit.Damage).ToString("N0", Inv) + (string.IsNullOrEmpty(hit.Type) ? string.Empty : " " + hit.Type);

            row.Add(WrappedNameCell(top[i].Key, "vdr-cell", null, nameMinWidth));
            row.Add(WrappedFixedCell(hitText, "vdr-cell", true, DeathHitWidth, null));
            row.Add(WrappedFixedCell(top[i].Value.ToString("N0", Inv), "vdr-cell", true, MobsAmountWidth, null));

            if (serverTable)
            {
                string who = null;
                bool hasWho = details && _data.DeathCauseMostBy.TryGetValue(top[i].Key, out who) && !string.IsNullOrEmpty(who);
                row.Add(WrappedFixedCell(hasWho ? who : "-", "vdr-cell", false, DeathByWhoWidth, hasWho && IsYou(who) ? "vdr-you" : null));
            }

            content.Add(row);
        }

        content.Add(MakeBlock(TableGap));
    }

    /// <summary>How much room the Death causes tab needs: one table for the server and one per player, each with at least one row.</summary>
    private float DeathCausesContentHeight()
    {
        float height = MobsTableHeight(GetTopFive(_data.DeathCauses).Length);
        foreach (ReportPlayer player in _deathPlayers)
            height += MobsTableHeight(GetTopFive(player.DeathCauses).Length);

        return height;
    }

    private static Label MakeHeading(string text)
    {
        return MakeLabel(text, "vdr-heading");
    }

    private KeyValuePair<string, int>[] GetTopFive(Dictionary<string, int> counts)
    {
        KeyValuePair<string, int>[] cached;
        if (_topCache.TryGetValue(counts, out cached))
            return cached;

        List<KeyValuePair<string, int>> rows = new List<KeyValuePair<string, int>>(counts);
        rows.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            int byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        cached = new KeyValuePair<string, int>[Math.Min(5, rows.Count)];
        for (int i = 0; i < cached.Length; i++)
            cached[i] = rows[i];

        _topCache[counts] = cached;
        return cached;
    }

    private int BossesKilledBy(string name)
    {
        int count;
        return name != null && _bossKillsByPlayer.TryGetValue(name, out count) ? count : 0;
    }

    private bool IsYou(string name)
    {
        return _localName.Length > 0 && string.Equals(name, _localName, StringComparison.OrdinalIgnoreCase);
    }

    private VisualElement MakeSortHeader(string text, int column, bool right, float width)
    {
        Btn button = new Btn(delegate { Guard(delegate { OnSortClicked(column); }); }, "vdr-sort-btn");
        button.Element.text = text;

        if (right)
            button.Element.AddToClassList("vdr-right");

        IStyle s = button.Element.style;

        if (width > 0f)
        {
            s.width = width;
            s.flexShrink = 0f;
        }
        else
        {
            s.flexGrow = 1f;
            s.flexShrink = 1f;
            s.minWidth = 0f;
        }

        return button.Element;
    }
}
