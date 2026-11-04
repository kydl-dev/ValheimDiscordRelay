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
    // ---------------------------------------------------------------- element helpers

    /// <summary>
    /// Gives an element a known starting point: no margin, padding, border or rounded corners, whatever a theme would say.
    /// </summary>
    private static void ResetBox(VisualElement element)
    {
        IStyle s = element.style;
        s.marginLeft = 0f;
        s.marginRight = 0f;
        s.marginTop = 0f;
        s.marginBottom = 0f;
        s.paddingLeft = 0f;
        s.paddingRight = 0f;
        s.paddingTop = 0f;
        s.paddingBottom = 0f;
        s.borderLeftWidth = 0f;
        s.borderRightWidth = 0f;
        s.borderTopWidth = 0f;
        s.borderBottomWidth = 0f;
        s.borderTopLeftRadius = 0f;
        s.borderTopRightRadius = 0f;
        s.borderBottomLeftRadius = 0f;
        s.borderBottomRightRadius = 0f;
    }

    /// <summary>The frame's thickness is part of the layout; its colour comes from the style sheet.</summary>
    private static void SetBorderWidth(VisualElement element, float width)
    {
        IStyle s = element.style;
        s.borderLeftWidth = width;
        s.borderRightWidth = width;
        s.borderTopWidth = width;
        s.borderBottomWidth = width;
    }

    private static void FillParent(VisualElement element)
    {
        IStyle s = element.style;
        s.position = Position.Absolute;
        s.left = 0f;
        s.right = 0f;
        s.top = 0f;
        s.bottom = 0f;
    }

    /// <summary>
    /// A text label with the given style classes. Only the behaviour is set here (one line, cut with "..." when too long,
    /// not clickable); how it looks is up to the style sheet.
    /// </summary>
    private static Label MakeLabel(string text, params string[] classes)
    {
        Label label = new Label(text);
        ResetBox(label);

        foreach (string cssClass in classes)
            label.AddToClassList(cssClass);

        IStyle s = label.style;
        s.whiteSpace = WhiteSpace.NoWrap;
        s.overflow = Overflow.Hidden;
        s.textOverflow = TextOverflow.Ellipsis;
        s.flexShrink = 0f;

        // Text never needs the pointer: clicks and wheel turns go to the element behind it.
        label.pickingMode = PickingMode.Ignore;
        return label;
    }

    /// <summary>One list row: a flex row of cells, RowHeight high, with an optional style class (vdr-row-alt, vdr-row-you).</summary>
    private static VisualElement MakeRow(string cssClass)
    {
        VisualElement row = new VisualElement();
        ResetBox(row);
        row.style.height = RowHeight;
        row.style.flexShrink = 0f;
        row.style.flexDirection = FlexDirection.Row;

        if (!string.IsNullOrEmpty(cssClass))
            row.AddToClassList(cssClass);

        return row;
    }

    /// <summary>
    /// A list row that is at least RowHeight high and as tall as its tallest cell (cells that wrap make it taller).
    /// </summary>
    private static VisualElement MakeFlexRow(string cssClass)
    {
        VisualElement row = new VisualElement();
        ResetBox(row);
        row.style.minHeight = RowHeight;
        row.style.flexShrink = 0f;
        row.style.flexDirection = FlexDirection.Row;

        if (!string.IsNullOrEmpty(cssClass))
            row.AddToClassList(cssClass);

        return row;
    }

    /// <summary>
    /// A text cell that takes the rest of the row and wraps its text onto more lines instead of cutting it with "...".
    /// The wrapping is behaviour, so it is set here and does not depend on the style sheet.
    /// </summary>
    private static Label WrappedCell(string text, string baseClass, string state)
    {
        Label cell = MakeLabel(text, baseClass, "vdr-wrap");

        if (!string.IsNullOrEmpty(state))
            cell.AddToClassList(state);

        IStyle s = cell.style;
        s.whiteSpace = WhiteSpace.Normal;
        s.textOverflow = TextOverflow.Clip;
        s.flexGrow = 1f;
        s.flexShrink = 1f;
        s.flexBasis = 0f;
        s.minWidth = 0f;
        return cell;
    }

    /// <summary>
    /// Like WrappedCell, but never narrower than <paramref name="minWidth"/>: the column that takes the rest of the row, with a
    /// minimum worked out from the text it holds.
    /// </summary>
    private static Label WrappedNameCell(string text, string baseClass, string state, float minWidth)
    {
        Label cell = WrappedCell(text, baseClass, state);
        cell.style.minWidth = minWidth;
        return cell;
    }

    /// <summary>
    /// A text cell of a fixed width whose text wraps onto more lines instead of being cut with "...". The row it is in must be
    /// a flex row (MakeFlexRow) so it grows with the cell.
    /// </summary>
    private static Label WrappedFixedCell(string text, string baseClass, bool right, float width, string state)
    {
        Label cell = MakeLabel(text, baseClass, "vdr-wrap");

        if (right)
            cell.AddToClassList("vdr-right");

        if (!string.IsNullOrEmpty(state))
            cell.AddToClassList(state);

        IStyle s = cell.style;
        s.whiteSpace = WhiteSpace.Normal;
        s.textOverflow = TextOverflow.Clip;
        s.width = Pixels(width);
        s.flexShrink = 0f;
        return cell;
    }

    /// <summary>
    /// Like WrappedFixedCell, but the width is a percentage of the row (so a header cell with the same percentage lines up with
    /// it). The row it is in must be a flex row (MakeFlexRow).
    /// </summary>
    private static Label WrappedPercentCell(string text, string baseClass, bool right, float percent, string state)
    {
        Label cell = MakeLabel(text, baseClass, "vdr-wrap");

        if (right)
            cell.AddToClassList("vdr-right");

        if (!string.IsNullOrEmpty(state))
            cell.AddToClassList(state);

        IStyle s = cell.style;
        s.whiteSpace = WhiteSpace.Normal;
        s.textOverflow = TextOverflow.Clip;
        s.width = Percent(percent);
        s.flexShrink = 0f;
        return cell;
    }

    private static VisualElement MakeBlock(float height)
    {
        VisualElement block = new VisualElement();
        ResetBox(block);
        block.style.height = height;
        block.style.flexShrink = 0f;
        return block;
    }

    /// <summary>
    /// One cell of a row. Width 0 means "takes the rest of the row" (and may shrink, so a long name is cut with "...").
    /// <paramref name="state"/> is an extra style class (vdr-you, vdr-online, vdr-death) or null.
    /// </summary>
    private static Label RowCell(string text, string baseClass, bool right, float width, bool widthIsPercent, string state)
    {
        Label cell = MakeLabel(text, baseClass);

        if (right)
            cell.AddToClassList("vdr-right");

        if (!string.IsNullOrEmpty(state))
            cell.AddToClassList(state);

        if (width > 0f)
        {
            cell.style.width = widthIsPercent ? Percent(width) : Pixels(width);
            cell.style.flexShrink = 0f;
        }
        else
        {
            cell.style.flexGrow = 1f;
            cell.style.flexShrink = 1f;
            cell.style.minWidth = 0f;
        }

        return cell;
    }

    private static Length Pixels(float value)
    {
        return new Length(value, LengthUnit.Pixel);
    }

    private static Length Percent(float value)
    {
        return new Length(value, LengthUnit.Percent);
    }

    /// <summary>
    /// Places a child at a fixed position inside a block. Height below 0 means "as tall as its content".
    /// </summary>
    private static void AddAbsolute(VisualElement parent, VisualElement child, Length left, Length width, float top, float height)
    {
        child.style.position = Position.Absolute;
        child.style.left = left;
        child.style.width = width;
        child.style.top = top;

        if (height >= 0f)
            child.style.height = height;

        parent.Add(child);
    }
}
