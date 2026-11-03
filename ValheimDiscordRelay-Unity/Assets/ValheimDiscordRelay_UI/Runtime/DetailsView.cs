using System.Collections.Generic;
using KydlCore.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// Detail blocks (DetailGroup / DetailRow) in the shared look (from WhipItUp's WeaponDetailsView, shared through the mod template): a heading
    /// per block; where a value changes with quality, a small header names the two columns (e.g. "Quality 1" | "Quality 4").
    /// </summary>
    internal static class DetailsView
    {
        const float LabelWidth = 210f;      // px at the 1080p reference of the shared panel
        const float ValueWidth = 140f;

        /// Builds the blocks. Expected outcome: an element with one heading per group (empty when there are none).
        internal static VisualElement Build(IList<DetailGroup> groups, string qualityHeader, string maxQualityHeader)
        {
            var box = new VisualElement { name = "kydl-details" };
            box.style.flexShrink = 0;
            int rows = 0;
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    box.Add(UiControls.Heading(g.Title));
                    bool hasMax = false;
                    foreach (var r in g.Rows) if (r.HasMax) { hasMax = true; break; }
                    if (hasMax) box.Add(ColumnHeader(qualityHeader, maxQualityHeader));
                    foreach (var r in g.Rows) { box.Add(StatRow(r, hasMax)); rows++; }
                }
            }
            UiLog.Verbose("DetailsView: " + (groups != null ? groups.Count : 0) + " group(s), " + rows + " row(s)");
            return box;
        }

        static VisualElement ColumnHeader(string q, string max)
        {
            var r = RowBox();
            r.Add(Cell("", LabelWidth, UiKit.TextDim, UiKit.SmallSize));
            r.Add(Cell(q ?? "", ValueWidth, UiKit.TextDim, UiKit.SmallSize));
            r.Add(Cell(max ?? "", ValueWidth, UiKit.TextDim, UiKit.SmallSize));
            return r;
        }

        /// label | value | (max-quality value). Only a row with a max value gets a fixed-width value cell; other values take the rest of the line.
        static VisualElement StatRow(DetailRow row, bool blockHasMax)
        {
            var r = RowBox();
            r.Add(Cell(row.Label, LabelWidth, UiKit.TextDim, UiKit.FontSize));
            r.Add(Cell(row.Value, blockHasMax && row.HasMax ? ValueWidth : -1f, UiKit.Text, UiKit.FontSize));
            if (blockHasMax && row.HasMax) r.Add(Cell(row.MaxValue, ValueWidth, UiKit.Gold, UiKit.FontSize));
            return r;
        }

        static VisualElement RowBox()
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row; r.style.alignItems = Align.FlexStart; r.style.flexShrink = 0;
            r.style.marginTop = 2; r.style.marginBottom = 2;
            return r;
        }

        static Label Cell(string content, float width, Color color, float size)
        {
            var l = UiKit.Label(content, size, color);
            l.style.whiteSpace = WhiteSpace.Normal;
            if (width >= 0f) { l.style.width = width; l.style.flexShrink = 0; }
            else { l.style.flexGrow = 1; l.style.flexShrink = 1; }
            return l;
        }
    }
}
