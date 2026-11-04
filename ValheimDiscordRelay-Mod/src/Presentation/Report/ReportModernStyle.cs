namespace ValheimDiscordRelay.Report;

/// <summary>
/// The "Modern" look of the in-game report window ([Weekly Report] Client - Window Style = Modern), as style sheet text.
///
/// It works exactly like the classic one (see ReportDefaultStyle): this text is applied first, and the player's own
/// BepInEx/config/ValheimDiscordRelay.report.modern.uss is read on top of it each time the window opens. The layout (the
/// heights of rows, tiles and tabs) is the same as in Classic, so only colours, corners, borders and fonts differ here.
/// The window itself adds a few decorations in this style only: the round icon in the title bar, the diamond on the line
/// under the title, and the key hints in the footer (classes vdr-badge, vdr-badge-bar, vdr-diamond, vdr-key, vdr-key-text).
///
/// Fonts: Valheim's Norse (rune) font for the title and the footer buttons only. Everything else stays on the window's
/// default font, because player names can contain characters the game's own fonts do not have.
/// </summary>
internal static class ReportModernStyle
{
    internal const string FileName = "ValheimDiscordRelay.report.modern.uss";

    internal const string Text = @"
:root {
    --vdr-panel: rgba(24, 19, 15, 0.98);
    --vdr-band: #1F1812;
    --vdr-inset: #130F0B;
    --vdr-card: #261E17;
    --vdr-card-hover: #32271C;
    --vdr-line: #3B2E20;
    --vdr-line-soft: rgba(255, 255, 255, 0.07);
    --vdr-gold: #EDB868;
    --vdr-accent: #E0A458;
    --vdr-accent-hover: #EEB86E;
    --vdr-accent-pressed: #C48E47;
    --vdr-on-accent: #1B1309;
    --vdr-text: #EDE3CF;
    --vdr-muted: #A39580;
    --vdr-online: #86C96B;
    --vdr-death: #EA7461;
    --vdr-warn: #F2A94E;
    --vdr-row-you: rgba(237, 184, 104, 0.13);
    --vdr-key-bg: #130F0B;
    --vdr-scroll-thumb: rgba(237, 184, 104, 0.40);
    --vdr-scroll-thumb-hover: rgba(237, 184, 104, 0.85);
}

/* The window: rounded, with a thin warm frame. */
.vdr-window {
    background-color: var(--vdr-panel);
    border-color: var(--vdr-line);
    border-radius: 12px;
}

/* Title bar: a slightly lighter band, the round icon, the title in Valheim's rune font, a quiet X. */
.vdr-title-bar { background-color: var(--vdr-band); }

.vdr-badge {
    background-color: rgba(237, 184, 104, 0.10);
    border-color: var(--vdr-accent);
    border-radius: 14px;
}

.vdr-badge-bar {
    background-color: var(--vdr-gold);
    border-radius: 1px;
}

.vdr-title {
    -unity-font-definition: heading;
    color: var(--vdr-gold);
    font-size: 22px;
    letter-spacing: 1px;
    -unity-text-align: middle-left;
}

.vdr-divider { background-color: var(--vdr-line); }
.vdr-diamond { background-color: var(--vdr-accent); }
.vdr-footer-line { background-color: var(--vdr-line-soft); }
.vdr-grip-mark { background-color: var(--vdr-line); }

/* Buttons: X, Refresh, Close. Refresh carries vdr-btn-primary (gold); Close is the quiet one. */
.vdr-btn {
    color: var(--vdr-text);
    font-size: 13px;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    padding-left: 6px;
    padding-right: 6px;
    border-radius: 7px;
}

.vdr-btn-close {
    background-color: rgba(0, 0, 0, 0);
    color: var(--vdr-muted);
}
.vdr-btn-close:hover {
    background-color: rgba(234, 116, 97, 0.20);
    color: var(--vdr-death);
}
.vdr-btn-close:active { background-color: rgba(234, 116, 97, 0.32); }

.vdr-btn-footer {
    -unity-font-definition: heading;
    font-size: 19px;
    letter-spacing: 0.5px;
    -unity-font-style: bold;
    background-color: var(--vdr-inset);
    border-width: 1px;
    border-color: var(--vdr-line);
}
.vdr-btn-footer:hover { background-color: var(--vdr-card-hover); }
.vdr-btn-footer:active { background-color: var(--vdr-card); }

.vdr-btn-primary {
    background-color: var(--vdr-accent);
    border-color: var(--vdr-accent);
    color: var(--vdr-on-accent);
}
.vdr-btn-primary:hover {
    background-color: var(--vdr-accent-hover);
    border-color: var(--vdr-accent-hover);
}
.vdr-btn-primary:active {
    background-color: var(--vdr-accent-pressed);
    border-color: var(--vdr-accent-pressed);
}

/* A button that cannot be used right now (Refresh while loading). */
.vdr-btn:disabled { opacity: 0.45; }
.vdr-btn-footer:disabled { background-color: var(--vdr-inset); }
.vdr-btn-primary:disabled {
    background-color: var(--vdr-accent);
    border-color: var(--vdr-accent);
}

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

/* The five summary tiles are rounded cards. */
.vdr-tile {
    background-color: var(--vdr-card);
    border-width: 1px;
    border-color: var(--vdr-line);
    border-radius: 10px;
}

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

/* The tabs are pills; the selected one is filled gold (vdr-tab-active is added to it). */
.vdr-tab {
    background-color: rgba(0, 0, 0, 0);
    border-width: 1px;
    border-color: var(--vdr-line);
    border-radius: 13px;
    color: var(--vdr-muted);
    font-size: 13px;
    -unity-font-style: normal;
    -unity-text-align: middle-center;
    padding-left: 6px;
    padding-right: 6px;
}

.vdr-tab:hover {
    background-color: var(--vdr-card-hover);
    color: var(--vdr-text);
}

.vdr-tab:active { background-color: var(--vdr-card); }

.vdr-tab.vdr-tab-active,
.vdr-tab.vdr-tab-active:hover,
.vdr-tab.vdr-tab-active:active {
    background-color: var(--vdr-accent);
    border-color: var(--vdr-accent);
    color: var(--vdr-on-accent);
    -unity-font-style: bold;
}

.vdr-legend {
    color: var(--vdr-muted);
    font-size: 11px;
    -unity-text-align: middle-right;
}

/* The column header of the tables: a dark inset bar. The Players header cells are buttons (click to sort). */
.vdr-table-header {
    background-color: var(--vdr-inset);
    border-radius: 7px;
}

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
    background-color: rgba(0, 0, 0, 0);
    border-radius: 7px;
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
    background-color: var(--vdr-card-hover);
    color: var(--vdr-text);
}

.vdr-sort-btn:active { background-color: var(--vdr-card); }

/* Table rows: every second row is a rounded card; your own row has a gold edge. */
.vdr-row-alt {
    background-color: var(--vdr-card);
    border-radius: 6px;
}

.vdr-row-you {
    background-color: var(--vdr-row-you);
    border-left-width: 2px;
    border-left-color: var(--vdr-gold);
    border-radius: 6px;
}

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

.vdr-heading {
    color: var(--vdr-gold);
    font-size: 13px;
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

/* A thin, rounded scrollbar thumb on an invisible track. left and right are the space on both sides of the thumb. */
.vdr-scroll-track { background-color: rgba(0, 0, 0, 0); }

.vdr-scroll-thumb {
    background-color: var(--vdr-scroll-thumb);
    border-radius: 3px;
    left: 5px;
    right: 5px;
}
.vdr-scroll-thumb:hover { background-color: var(--vdr-scroll-thumb-hover); }
.vdr-scroll-thumb:active { background-color: var(--vdr-scroll-thumb-hover); }

/* The footer: status line, key hints (or the opt-out note), and the warning that replaces them when a refresh failed. */
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

.vdr-key {
    background-color: var(--vdr-key-bg);
    border-width: 1px;
    border-color: var(--vdr-line);
    border-radius: 4px;
    color: var(--vdr-muted);
    font-size: 10px;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    padding-left: 6px;
    padding-right: 6px;
}

.vdr-key-text {
    color: var(--vdr-muted);
    font-size: 11px;
    -unity-text-align: middle-left;
}

.vdr-warn {
    color: var(--vdr-warn);
    font-size: 12px;
    -unity-text-align: upper-left;
}

/*
  To use Valheim's serif text font for everything, remove the comment marks around the next rule. Player names with
  characters the game's fonts do not have may then show as boxes.

.vdr-window { -unity-font-definition: body; }
*/
";

    internal const string FileHeader = @"/*
  Valheim Discord Relay - style sheet of the in-game weekly report window, MODERN style
  ([Weekly Report] Client - Window Style = Modern).

  This file is read when the window opens (so: edit, close the window, open it again - no game restart needed).
  The built-in look is applied first and the rules below come after it, so what is written here wins.
  Delete this file to get the original back (it is written again with the default rules). If you only want to change a
  few things, you can delete everything except those rules.

  It is a subset of Unity's USS (CSS for UI Toolkit):
    - selectors: .class  #name  Label  Button  *  (combined: Label.vdr-cell), descendant (a b) and child (a > b),
      comma lists, and the pseudo-classes :hover :active :disabled :enabled
    - variables: --name: value;  inside :root { }  and used as var(--name)
    - values: #rgb #rrggbb #rrggbbaa rgb(r, g, b) rgba(r, g, b, a), 12px, 50%, numbers, keywords, initial
    - properties: color, background-color, font-size, letter-spacing, -unity-font-style (normal | bold | italic |
      bold-and-italic), -unity-text-align (upper-left ... middle-center ... lower-right), opacity, margin, padding,
      border-width, border-color, border-radius and most other USS properties
    - fonts: -unity-font-definition: heading | heading-bold | body | body-bold (Valheim's own fonts: Norse and
      Averia Serif). Nothing else can be used as a font.
  Not supported: @import and other @rules, url(), transitions, the flex and border shorthands. Lines that are not
  understood are skipped and listed as warnings in the BepInEx log with their line number.
  Heights of rows, tiles and tabs are fixed in the mod: a much larger font-size will be cut off.
*/

";

    /// <summary>What is written to the config folder the first time: the explanation, then the default rules.</summary>
    internal static string FileText
    {
        get { return FileHeader + Text.TrimStart(); }
    }
}
