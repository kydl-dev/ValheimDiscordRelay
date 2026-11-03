namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// The look of the in-game report window, as style sheet text (see ReportStyleSheet for what is supported).
    ///
    /// <see cref="Text"/> is always applied first. The same text, with an explanation on top, is written once to
    /// BepInEx/config/ValheimDiscordRelay.report.uss; that file is read after the built-in rules, so anything in it wins,
    /// and it is read again each time the window opens (edit, close the window, press the key again).
    ///
    /// Sizes that decide the layout (the heights of rows, tiles and tabs, the window's minimum size) are fixed in the code;
    /// colours, fonts, text alignment, padding and borders are meant to be changed here.
    /// </summary>
    internal static class ReportDefaultStyle
    {
        internal const string FileName = "ValheimDiscordRelay.report.uss";

        internal const string FileHeader = @"/*
  Valheim Discord Relay - style sheet of the in-game weekly report window.

  This file is read when the window opens (so: edit, close the window, open it again - no game restart needed).
  The built-in look is applied first and the rules below come after it, so what is written here wins.
  Delete this file to get the original back (it is written again with the default rules). If you only want to change a
  few things, you can delete everything except those rules.

  It is a subset of Unity's USS (CSS for UI Toolkit):
    - selectors: .class  #name  Label  Button  *  (combined: Label.vdr-cell), descendant (a b) and child (a > b),
      comma lists, and the pseudo-classes :hover :active :disabled :enabled
    - variables: --name: value;  inside :root { }  and used as var(--name)
    - values: #rgb #rrggbb #rrggbbaa rgb(r, g, b) rgba(r, g, b, a), 12px, 50%, numbers, keywords, initial
    - properties: color, background-color, font-size, -unity-font-style (normal | bold | italic | bold-and-italic),
      -unity-text-align (upper-left ... middle-center ... lower-right), opacity, margin, padding, border-width,
      border-color, border-radius and most other USS properties
  Not supported: @import and other @rules, url(), transitions, the flex and border shorthands. Lines that are not
  understood are skipped and listed as warnings in the BepInEx log with their line number.
  Fonts: -unity-font-definition: heading | heading-bold | body | body-bold selects one of Valheim's own fonts (Norse and
  Averia Serif); nothing else can be used as a font. Heights of rows, tiles and tabs are fixed in the mod: a much larger
  font-size will be cut off.
*/

";

        internal const string Text = @"
:root {
    --vdr-panel: rgba(19, 16, 13, 0.97);
    --vdr-tile: #221D17;
    --vdr-header: #2B241A;
    --vdr-header-hover: #42331F;
    --vdr-border: #8C6B33;
    --vdr-dim-line: rgba(255, 255, 255, 0.10);
    --vdr-gold: #FFC759;
    --vdr-text: #EDE6D6;
    --vdr-muted: #A39985;
    --vdr-online: #8CE080;
    --vdr-death: #F28575;
    --vdr-warn: #FFA84D;
    --vdr-row-alt: rgba(255, 255, 255, 0.035);
    --vdr-row-you: rgba(255, 199, 89, 0.14);
    --vdr-btn: #382B1C;
    --vdr-btn-hover: #574226;
    --vdr-btn-pressed: #75572B;
    --vdr-close: #57261F;
    --vdr-close-hover: #8C3326;
    --vdr-tab: #1F1A14;
    --vdr-tab-hover: #33291C;
    --vdr-tab-active: #4D381C;
    --vdr-scroll-track: rgba(255, 255, 255, 0.05);
    --vdr-scroll-thumb: rgba(140, 107, 51, 0.85);
    --vdr-scroll-thumb-hover: #BF9447;
}

/* The window itself: panel colour and frame colour. */
.vdr-window {
    background-color: var(--vdr-panel);
    border-color: var(--vdr-border);
}

/* The line under the title, the line above the footer, and the marks of the resize corner. */
.vdr-divider { background-color: var(--vdr-border); }
.vdr-footer-line { background-color: var(--vdr-dim-line); }
.vdr-grip-mark { background-color: var(--vdr-border); }

.vdr-title {
    color: var(--vdr-gold);
    font-size: 20px;
    -unity-font-style: bold;
    -unity-text-align: middle-left;
}

/* Buttons: the X, Refresh and Close. */
.vdr-btn {
    color: var(--vdr-text);
    font-size: 13px;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    padding-left: 6px;
    padding-right: 6px;
}

.vdr-btn-footer { background-color: var(--vdr-btn); }
.vdr-btn-footer:hover { background-color: var(--vdr-btn-hover); }
.vdr-btn-footer:active { background-color: var(--vdr-btn-pressed); }

.vdr-btn-close { background-color: var(--vdr-close); }
.vdr-btn-close:hover { background-color: var(--vdr-close-hover); }
.vdr-btn-close:active { background-color: var(--vdr-close-hover); }

/* A button that cannot be used right now (Refresh while loading). */
.vdr-btn:disabled {
    background-color: var(--vdr-btn);
    opacity: 0.5;
}

/* Texts above the tabs. */
.vdr-subtitle {
    color: var(--vdr-muted);
    font-size: 12px;
    -unity-text-align: middle-left;
}

.vdr-empty-text, .vdr-list-message {
    color: var(--vdr-muted);
    font-size: 14px;
    -unity-text-align: middle-center;
    padding-left: 8px;
    padding-right: 8px;
}

/* The five summary tiles. */
.vdr-tile { background-color: var(--vdr-tile); }

.vdr-tile-value {
    color: var(--vdr-text);
    font-size: 22px;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    padding-left: 2px;
    padding-right: 2px;
}

.vdr-tile-value-death { color: var(--vdr-death); }

.vdr-tile-caption {
    color: var(--vdr-muted);
    font-size: 11px;
    -unity-text-align: middle-center;
    padding-left: 2px;
    padding-right: 2px;
}

/* The tabs. vdr-tab-active is added to the selected one. */
.vdr-tab {
    background-color: var(--vdr-tab);
    color: var(--vdr-muted);
    font-size: 13px;
    -unity-font-style: normal;
    -unity-text-align: middle-center;
    padding-left: 6px;
    padding-right: 6px;
}

.vdr-tab:hover {
    background-color: var(--vdr-tab-hover);
    color: var(--vdr-text);
}

.vdr-tab:active { background-color: var(--vdr-tab-active); }

.vdr-tab.vdr-tab-active,
.vdr-tab.vdr-tab-active:hover,
.vdr-tab.vdr-tab-active:active {
    background-color: var(--vdr-tab-active);
    color: var(--vdr-gold);
    -unity-font-style: bold;
}

.vdr-legend {
    color: var(--vdr-muted);
    font-size: 11px;
    -unity-text-align: middle-right;
}

/* The column header of the tables. The Players header cells are buttons (click to sort). */
.vdr-table-header { background-color: var(--vdr-header); }

.vdr-head-cell {
    color: var(--vdr-gold);
    font-size: 12px;
    -unity-font-style: bold;
    -unity-text-align: middle-left;
    padding-left: 8px;
    padding-right: 0;
}

.vdr-head-cell.vdr-right {
    -unity-text-align: middle-right;
    padding-left: 0;
    padding-right: 10px;
}

.vdr-sort-btn {
    background-color: var(--vdr-header);
    color: var(--vdr-gold);
    font-size: 12px;
    -unity-font-style: bold;
    -unity-text-align: middle-left;
    padding-left: 8px;
    padding-right: 0;
}

.vdr-sort-btn.vdr-right {
    -unity-text-align: middle-right;
    padding-left: 0;
    padding-right: 10px;
}

.vdr-sort-btn:hover {
    background-color: var(--vdr-header-hover);
    color: var(--vdr-text);
}

.vdr-sort-btn:active { background-color: var(--vdr-header-hover); }

/* Table rows: every second row, and the row of the player looking at the window. */
.vdr-row-alt { background-color: var(--vdr-row-alt); }
.vdr-row-you { background-color: var(--vdr-row-you); }

/* Table cells. vdr-right is added to right-aligned (number) cells. */
.vdr-cell {
    color: var(--vdr-text);
    font-size: 13px;
    -unity-text-align: middle-left;
    padding-left: 8px;
    padding-right: 4px;
}

.vdr-cell.vdr-right {
    -unity-text-align: middle-right;
    padding-left: 0;
    padding-right: 10px;
}

/* A cell whose text may wrap onto a second line (player names): a little air above and below. */
.vdr-wrap {
    padding-top: 2px;
    padding-bottom: 2px;
}

.vdr-cell.vdr-online { color: var(--vdr-online); }
.vdr-cell.vdr-death { color: var(--vdr-death); }

.vdr-cell.vdr-you {
    color: var(--vdr-gold);
    -unity-font-style: bold;
}

/* Mobs killed (table headings), Killer mobs and Death causes: the headings and the ranked lines. */
.vdr-heading {
    color: var(--vdr-gold);
    font-size: 12px;
    -unity-font-style: bold;
    -unity-text-align: middle-left;
    padding-left: 8px;
}

.vdr-rank-line {
    color: var(--vdr-text);
    font-size: 13px;
    -unity-text-align: middle-left;
    padding-left: 8px;
    padding-right: 4px;
}

/* The scrollbar. */
.vdr-scroll-track { background-color: var(--vdr-scroll-track); }
.vdr-scroll-thumb { background-color: var(--vdr-scroll-thumb); }
.vdr-scroll-thumb:hover { background-color: var(--vdr-scroll-thumb-hover); }
.vdr-scroll-thumb:active { background-color: var(--vdr-scroll-thumb-hover); }

/* The footer: status line, hint line, and the warning that replaces them when a refresh failed. */
.vdr-status {
    color: var(--vdr-text);
    font-size: 12px;
    -unity-text-align: middle-left;
}

.vdr-hint {
    color: var(--vdr-muted);
    font-size: 11px;
    -unity-text-align: middle-left;
}

.vdr-warn {
    color: var(--vdr-warn);
    font-size: 12px;
    -unity-text-align: upper-left;
}
";

        /// <summary>What is written to the config folder the first time: the explanation, then the default rules.</summary>
        internal static string FileText
        {
            get { return FileHeader + Text.TrimStart(); }
        }

        // ---------------------------------------------------------------- the two styles

        /// <summary>The built-in rules of a style (always applied first).</summary>
        internal static string BuiltInText(ReportWindowStyle style)
        {
            return style == ReportWindowStyle.Modern ? ReportModernStyle.Text : Text;
        }

        /// <summary>The player's style sheet file of a style, in the BepInEx config folder.</summary>
        internal static string FileNameFor(ReportWindowStyle style)
        {
            return style == ReportWindowStyle.Modern ? ReportModernStyle.FileName : FileName;
        }

        /// <summary>The text written to that file the first time (explanation, then the built-in rules).</summary>
        internal static string FileTextFor(ReportWindowStyle style)
        {
            return style == ReportWindowStyle.Modern ? ReportModernStyle.FileText : FileText;
        }
    }
}
