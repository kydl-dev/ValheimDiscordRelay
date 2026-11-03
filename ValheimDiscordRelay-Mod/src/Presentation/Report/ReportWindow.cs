using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// The in-game weekly report window: the same numbers as the Discord report, as they stand right now.
    ///
    /// Opened and closed with [Weekly Report] Client - Window Key (default Home). It asks the server for a snapshot when it
    /// opens (and every 30 seconds, or on the Refresh button) and shows:
    ///   - summary tiles: players joined, mob kills, deaths, bosses defeated, highest damage dealt
    ///   - a Players tab: one row per player (kills, deaths, damage), sortable by clicking a column header
    ///   - a Bosses tab: one row per boss kill with the killing blow, the highest hit the boss received, how many players it killed, and when
    ///   - a Mobs killed tab: the top five mobs killed on the server and by each player, with the highest hit each kind of mob received
    ///   - a Killer mobs tab: the top five mobs that killed players on the server and each player, and who died to each of them most
    ///   - a Death causes tab: the most common causes of death on the server and for each player, with the biggest finishing hit of each
    ///     cause and, for the server, who died of it most
    ///   - a Highest hits tab
    ///
    /// Drawn with Unity's UI Toolkit (UIElements): a runtime panel (UIDocument + PanelSettings) that this component creates
    /// from code the first time the window opens. There is no UXML and no asset bundle: the element tree is built here and
    /// carries "vdr-*" class names.
    ///
    /// The look comes from a style sheet (see ReportStyleSheet and ReportDefaultStyle): a built-in one, plus the player's own
    /// BepInEx/config/ValheimDiscordRelay.report.uss, read again whenever the window opens. Unity's runtime cannot read USS
    /// text, so ReportStyleSheet reads it and applies the result as inline styles. The code only decides the layout (the
    /// structure and the fixed heights); colours, fonts, alignment, padding and borders belong to the style sheet. The
    /// built-in ScrollView is not used (its structure comes from a theme style sheet that a mod cannot ship): see ScrollPane.
    ///
    /// Unlike IMGUI nothing is redrawn every frame: the element tree is built once, the cheap parts (texts, button states,
    /// window rectangle) are refreshed every frame while the window is open, and the list under the tabs is rebuilt only when
    /// the data, the tab, the sort order or who is online changes.
    ///
    /// While the window is open the player does not move or attack, and the mouse cursor is free (see ReportWindowPatches).
    /// </summary>
    public sealed partial class ReportWindow : MonoBehaviour
    {
        // ---------------------------------------------------------------- layout (panel pixels, before the window scale)
        //
        // These are the same numbers the IMGUI window used. The element tree is laid out with flexbox, but the fixed parts
        // (title, tiles, tabs, column header, footer) have exactly these heights, so RequiredHeight() still adds up.

        private const float Frame = 1f;
        private const float ResizeGrip = 14f;
        private const float ResizeEdge = 10f;
        private const float ScrollbarWidth = 14f;
        private const float PanelSortingOrder = 1000f;

        private const int GripCorner = 0;
        private const int GripRightEdge = 1;
        private const int GripBottomEdge = 2;

        // The window is never smaller than its content needs:
        //   - MinWidth is exactly what the row of summary tiles needs (the last tile carries "Top damage: <name>"),
        //   - RequiredHeight() adds up the fixed parts and the list the current tab shows. A list counts for at most
        //     MaxRequiredListHeight (a longer one scrolls) and at least MinListHeight.
        // The default is compact: as large as the biggest minimum, so nothing jumps when the player switches tabs, plus a little
        // room so the Players table (five columns) leaves the name column a comfortable width.
        private const float TileLastMin = 168f;
        private const float MinWidth = 2f * Frame + 2f * Pad + 4f * TileSmall + 4f * TileGap + TileLastMin;
        private const float DefaultWidth = MinWidth + 80f;
        private const float MinListHeight = 3f * RowHeight;
        private const float MaxRequiredListHeight = 10f * RowHeight;
        private const float DefaultHeight = 2f * Frame + ListTop + MaxRequiredListHeight + FooterHeight + 4f;
        private const float EmptyBodyTop = 100f;
        private const float EmptyBodyHeight = 160f;

        private const float Pad = 16f;
        private const float TitleBarHeight = 44f;
        private const float SubtitleGap = 5f;
        private const float SubtitleHeight = 18f;
        private const float TilesGap = 8f;
        private const float TileHeight = 58f;
        private const float TileGap = 6f;
        private const float TileSmall = 104f;
        private const float TabsGap = 2f;
        private const float TabRowGap = 4f;
        private const float TabHeight = 26f;
        private const float LegendGap = 2f;
        private const float LegendHeight = 18f;
        private const float HeaderGap = 4f;
        private const float HeaderHeight = 24f;
        private const float FooterHeight = 54f;
        private const float RowHeight = 22f;
        private const float ButtonWidth = 112f;

        // Where the list starts (counted from the top of the window) for tabs with a column header and for tabs without one.
        private const float HeaderTop = 234f;
        private const float ListTop = HeaderTop + HeaderHeight;

        private const float ColMobsWidth = 108f;
        private const float ColBossesWidth = 124f;
        private const float ColDeathsWidth = 76f;
        private const float ColDamageWidth = 120f;

        // The Bosses tab: the Boss column takes the rest of the row, the others have a fixed width. Every text cell wraps onto
        // more lines instead of being cut with "...", so the row grows with its tallest cell.
        private const float ColKillerWidth = 128f;
        private const float ColBossHitWidth = 196f;
        private const float ColBossKillsWidth = 56f;
        private const float ColWhenWidth = 124f;

        /// <summary>What a Bosses row is counted as when the window works out how much room the list needs (two lines of text).</summary>
        private const float BossRowHeight = 40f;

        // The Mobs killed tab: a table for the server and one per player. The mob name column takes the rest of the row but is never
        // narrower than its longest name needs (estimated from the text, at most MobsNameMinWidthCap: a longer name wraps onto a
        // second line instead of being cut with "..."). The other columns have a fixed width and wrap too.
        private const float MobsNameMinWidthCap = 200f;
        private const float MobsNameMinWidthFloor = 90f;
        private const float MobsAmountWidth = 70f;
        private const float MobsHitWidth = 96f;
        private const float MobsByWhoWidth = 130f;
        private const float MobsMostDeathsWidth = 150f;

        // The Death causes tab: Cause name | Finishing hit/damage | Amount, and for the server table Highest amount by who.
        private const float DeathHitWidth = 112f;
        private const float DeathByWhoWidth = 150f;
        private const float TableHeadingHeight = 22f;
        private const float TableGap = 12f;

        /// <summary>Rough width of one character of the cell font (13 px) and the padding around a cell's text, for the name column's minimum.</summary>
        private const float EstimatedCharWidth = 7.2f;
        private const float EstimatedCellPadding = 16f;

        // ---------------------------------------------------------------- behaviour

        private const float AutoRefreshSeconds = 30f;
        private const float RefreshCooldownSeconds = 3f;
        private const float ReplyTimeoutSeconds = 8f;
        private const float AcceptReplyWithinSeconds = 30f;
        private const float PresenceRefreshSeconds = 5f;

        private const int TabPlayers = 0;
        private const int TabBosses = 1;
        private const int TabMobsKilled = 2;
        private const int TabKillerMobs = 3;
        private const int TabDeathCauses = 4;
        private const int TabHighestHits = 5;
        private const int TabCount = 6;

        private const int ColName = 0;
        private const int ColKills = 1;
        private const int ColDeaths = 2;
        private const int ColDamage = 3;
        private const int ColBosses = 4;

        private const string NoAnswerText =
            "No answer from the server. Is Valheim Discord Relay (this version or newer) installed on the server?";

        private const string BadReplyText =
            "The server's answer could not be read. The mod on the server may be a different version.";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- state

        private static ReportWindow _instance;

        private bool _open;
        private Rect _rect = new(100f, 100f, DefaultWidth, DefaultHeight);
        private bool _rectPlaced;

        // The size the player wants (loaded from, and saved to, [Weekly Report] Client - Window Width / Height). The window is
        // drawn at this size, or larger when the content needs more room (see RequiredHeight).
        private float _prefWidth = DefaultWidth;
        private float _prefHeight = DefaultHeight;
        private float _lastHeight;

        private bool _resizing;
        private Vector2 _resizeStartPointer;
        private Rect _resizeStartRect;

        private bool _dragging;
        private Vector2 _dragStartPointer;
        private Rect _dragStartRect;

        private ReportWindowData _data;
        private readonly List<ReportPlayer> _rows = new();
        private readonly List<ReportBossKill> _bossRows = new();
        private readonly List<ReportPlayer> _hitRows = new();
        private readonly List<ReportPlayer> _killPlayers = new();
        private readonly List<ReportPlayer> _killerPlayers = new();
        private readonly List<ReportPlayer> _deathPlayers = new();

        // Boss kills per player (the player who landed the killing blow), counted from the snapshot's boss kill list.
        private readonly Dictionary<string, int> _bossKillsByPlayer = new(StringComparer.OrdinalIgnoreCase);

        // The five biggest entries of a count table, worked out once per snapshot instead of on every rebuild.
        private readonly Dictionary<Dictionary<string, int>, KeyValuePair<string, int>[]> _topCache = new();

        private int _tab = TabPlayers;
        private int _sortColumn = ColKills;
        private bool _sortDesc = true;

        // Where each tab was scrolled to, so a refresh (or a visit to another tab and back) does not jump to the top.
        private readonly float[] _scrollOffsets = new float[TabCount];
        private int _builtTab = -1;
        private bool _listDirty;

        private bool _awaiting;
        private float _lastRequestTime = -1000f;
        private float _nextAutoRefresh;
        private float _nextPresenceRefresh;
        private float _nextTextRebuild;
        private string _message;

        // Cached texts (rebuilt when data arrives, and once a second for the "in 2d 3h" countdown).
        private string _subtitleText = string.Empty;
        private string _nextReportText = string.Empty;
        private string _updatedText = string.Empty;
        private string _tileJoined = "0";
        private string _tileKills = "0";
        private string _tileDeaths = "0";
        private string _tileBosses = "0";
        private string _tileTopValue = "-";
        private string _tileTopCaption = "Top damage";
        private string _tabPlayersLabel = "Players";
        private string _tabBossesLabel = "Bosses";
        private string _hdrName = "Player";
        private string _hdrKills = "Mobs Killed";
        private string _hdrBosses = "Bosses killed";
        private string _hdrDeaths = "Deaths";
        private string _hdrDamage = "Total Damage";

        private string _localName = string.Empty;
        private readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);

        private int _failures;
        private float _nextFailureLog;

        // ---------------------------------------------------------------- UI Toolkit objects (created on the first open)

        private Font _font;
        private PanelSettings _panelSettings;
        private GameObject _uiObject;
        private UIDocument _document;
        private float _appliedScale = -1f;

        private VisualElement _window;
        private VisualElement _titleBar;
        private VisualElement _emptyBody;
        private Label _emptyLabel;
        private VisualElement _reportBody;
        private Label _subtitleLabel;
        private Label _nextReportLabel;
        private readonly Label[] _tileValues = new Label[5];
        private readonly Label[] _tileCaptions = new Label[5];
        private readonly Btn[] _tabButtons = new Btn[TabCount];
        private readonly bool[] _tabStyledActive = new bool[TabCount];
        private Label _legendLabel;
        private VisualElement _tableHeader;
        private VisualElement _listHost;
        private ScrollPane _scroll;
        private Label _listMessage;
        private VisualElement _footerInfo;
        private Label _statusLabel;
        private Label _hintLabel;
        private Label _warnLabel;
        private Btn _refreshButton;

        // The Modern style only: the key hints in the footer.
        private VisualElement _keysRow;
        private VisualElement _toggleKeyGroup;
        private Label _toggleKeyLabel;

        // The style the element tree was built for (Classic or Modern). The tree is rebuilt when the setting changes and the window opens.
        private ReportWindowStyle _builtStyle = ReportWindowStyle.Classic;
        private bool _modern;

        private ReportStyleSheet _styles;
        private DateTime _styleStamp = DateTime.MinValue;

        // ---------------------------------------------------------------- static entry points

        /// <summary>True while the window is open. Read by the Harmony patches that free the cursor and block the controls.</summary>
        internal static bool IsOpen
        {
            get { return _instance != null && _instance._open; }
        }

        /// <summary>Adds the window to the plugin's GameObject. Called once from the plugin Awake().</summary>
        internal static void Create(GameObject host)
        {
            if (_instance != null || host == null)
                return;

            host.AddComponent<ReportWindow>();
        }

        /// <summary>Closes the window (Escape, from the Harmony patch).</summary>
        internal static void Close()
        {
            ReportWindow window = _instance;
            if (window != null)
                window.SetOpen(false);
        }

        /// <summary>
        /// The server's answer to a snapshot request. Null means the answer could not be read.
        /// </summary>
        internal static void Receive(ReportWindowData data)
        {
            ReportWindow window = _instance;
            if (window != null)
                window.OnData(data);
        }

        // ---------------------------------------------------------------- Unity messages

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;

            _open = false;
            DestroyUi();
        }

        private void Update()
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet != null && znet.IsDedicated())
                    return;

                if (_open)
                {
                    if (!CanStayOpen())
                    {
                        SetOpen(false);
                    }
                    else
                    {
                        TickOpen();
                        UpdateUi();
                        _failures = 0;
                    }
                }

                if (HotkeyPressed())
                {
                    if (_open)
                        SetOpen(false);
                    else
                        TryOpen();
                }
            }
            catch (Exception ex)
            {
                ReportFailure(ex);

                // A window that keeps failing would flood the log every frame: give up on it.
                if (_open)
                {
                    _failures++;
                    if (_failures >= 3)
                    {
                        _failures = 0;
                        SetOpen(false);
                    }
                }
            }
        }

    }
}
