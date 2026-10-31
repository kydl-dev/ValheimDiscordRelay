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
    public sealed class ReportWindow : MonoBehaviour
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

        /// <summary>Adds the window to the plugin's GameObject. Called once from ClientPlugin.Awake().</summary>
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

        // ---------------------------------------------------------------- opening, closing, refreshing

        private static bool HotkeyPressed()
        {
            ConfigEntry<KeyboardShortcut> entry = ReportConfig.WindowKey;
            if (entry == null)
                return false;

            KeyboardShortcut shortcut = entry.Value;
            KeyCode mainKey = shortcut.MainKey;
            if (mainKey == KeyCode.None)
                return false;

            // The shortcut is complete on the frame its last key goes down; the order of the keys does not matter.
            bool allHeld = Input.GetKey(mainKey);
            bool anyWentDown = Input.GetKeyDown(mainKey);

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                    allHeld = false;

                if (Input.GetKeyDown(modifier))
                    anyWentDown = true;
            }

            if (!allHeld || !anyWentDown)
                return false;

            // Keys typed into the chat or the console are not hotkeys.
            if (Chat.instance != null && Chat.instance.HasFocus())
                return false;

            if (global::Console.IsVisible() || global::TextInput.IsVisible())
                return false;

            return true;
        }

        /// <summary>
        /// The window needs a player in the world and no other full-screen game UI on top.
        /// </summary>
        private static bool CanStayOpen()
        {
            if (Player.m_localPlayer == null)
                return false;

            if (Menu.IsVisible() || InventoryGui.IsVisible() || StoreGui.IsVisible() || Minimap.IsOpen())
                return false;

            return true;
        }

        private void TryOpen()
        {
            if (ZNet.instance == null || !CanStayOpen())
                return;

            if (ReportConfig.Enabled != null && !ReportConfig.Enabled.Value)
            {
                Notify("The weekly report is turned off on this server.");
                return;
            }

            if (ReportConfig.WindowEnabled != null && !ReportConfig.WindowEnabled.Value)
            {
                Notify("The in-game weekly report is turned off on this server.");
                return;
            }

            SetOpen(true);
        }

        private void SetOpen(bool open)
        {
            if (open == _open)
                return;

            // [Weekly Report] Client - Window Style changed since the window was built: build it again for the new style.
            if (open && _window != null && _builtStyle != CurrentStyle())
                DestroyUi();

            if (open && !EnsureUi())
            {
                Notify("The weekly report window could not be created. See the BepInEx log.");
                return;
            }

            _open = open;
            _resizing = false;
            _dragging = false;

            if (!open)
            {
                _awaiting = false;

                if (_window != null)
                    _window.style.display = DisplayStyle.None;

                return;
            }

            _message = null;

            PositionWindow();
            RefreshPresence();
            RequestNow();
            RebuildTexts();

            ReloadStylesIfChanged();

            _window.style.display = DisplayStyle.Flex;
            _listDirty = true;
            UpdateUi();
        }

        /// <summary>
        /// Per-frame work while the window is open: reply timeout, automatic refresh, who is online, the countdown text.
        /// </summary>
        private void TickOpen()
        {
            float now = Time.unscaledTime;

            if (_awaiting && now - _lastRequestTime > ReplyTimeoutSeconds)
            {
                _awaiting = false;
                _message = _data == null ? NoAnswerText : "The server did not answer the last refresh. The numbers shown may be out of date.";
            }

            if (!_awaiting && now >= _nextAutoRefresh)
                RequestNow();

            if (now >= _nextPresenceRefresh)
                RefreshPresence();

            if (now >= _nextTextRebuild)
                RebuildTexts();
        }

        private void RequestNow()
        {
            float now = Time.unscaledTime;
            _lastRequestTime = now;
            _nextAutoRefresh = now + AutoRefreshSeconds;

            // Set before asking: when this game is the server the answer arrives before RequestSnapshot() returns.
            _awaiting = true;

            if (!ReportWindowRpc.RequestSnapshot())
            {
                _awaiting = false;
                _message = "Not connected to a server.";
            }
        }

        private void OnData(ReportWindowData data)
        {
            if (!_open)
                return;

            // Only an answer to a recent request counts.
            if (Time.unscaledTime - _lastRequestTime > AcceptReplyWithinSeconds)
                return;

            _awaiting = false;

            if (data == null)
            {
                ClearData(BadReplyText);
                return;
            }

            if (data.Status != ReportProtocol.SnapshotStatusOk)
            {
                ClearData(StatusText(data.Status));
                return;
            }

            _data = data;
            _message = null;
            _updatedText = "Updated " + DateTime.Now.ToString("HH:mm:ss", Inv);

            _tileJoined = data.JoinedTotal.ToString("N0", Inv);
            _tileKills = data.KillsTotal.ToString("N0", Inv);
            _tileDeaths = data.DeathsTotal.ToString("N0", Inv);
            _tileBosses = data.BossTotal.ToString("N0", Inv);

            if (data.TopDamage > 0.0)
            {
                _tileTopValue = Math.Round(data.TopDamage).ToString("N0", Inv);
                _tileTopCaption = "Top damage: " + Truncate(data.TopDamageName, 14);
            }
            else
            {
                _tileTopValue = "-";
                _tileTopCaption = "Top damage";
            }

            int listedPlayers = data.Players.Count + data.HiddenPlayers;
            _tabPlayersLabel = "Players (" + listedPlayers.ToString(Inv) + ")";
            _tabBossesLabel = "Bosses (" + data.BossTotal.ToString(Inv) + ")";

            RebuildRows();
            UpdateHeaderLabels();
            RebuildTexts();
        }

        private void ClearData(string message)
        {
            _data = null;
            _message = message;
            _rows.Clear();
            _bossRows.Clear();
            _hitRows.Clear();
            _killPlayers.Clear();
            _killerPlayers.Clear();
            _deathPlayers.Clear();
            _bossKillsByPlayer.Clear();
            _topCache.Clear();
            _listDirty = true;
            RebuildTexts();
        }

        private static string StatusText(int status)
        {
            if (status == ReportProtocol.SnapshotStatusReportDisabled)
                return "The weekly report is turned off on this server.";

            if (status == ReportProtocol.SnapshotStatusWindowDisabled)
                return "The server admin has turned off the in-game weekly report.";

            return BadReplyText;
        }

        private static void Notify(string text)
        {
            try
            {
                if (MessageHud.instance != null)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
            }
            catch (Exception)
            {
                // A notice that cannot be shown is not worth a log line.
            }
        }

        private void ReportFailure(Exception ex)
        {
            // Throttled: an error in the per-frame update repeats every frame.
            float now = Time.unscaledTime;
            if (now < _nextFailureLog)
                return;

            _nextFailureLog = now + 30f;
            RelayDiagnostics.Error("[Report] The in-game weekly report window failed.", ex);
        }

        /// <summary>
        /// Runs a click handler; a failure is logged (throttled) instead of surfacing as a raw Unity exception.
        /// </summary>
        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        // ---------------------------------------------------------------- clicks

        private void OnTabClicked(int tab)
        {
            if (_tab == tab)
                return;

            _tab = tab;
            _listDirty = true;
        }

        private void OnSortClicked(int column)
        {
            if (column == _sortColumn)
            {
                _sortDesc = !_sortDesc;
            }
            else
            {
                _sortColumn = column;
                _sortDesc = column != ColName;
            }

            RebuildRows();
            UpdateHeaderLabels();
        }

        private void OnRefreshClicked()
        {
            if (!_awaiting && Time.unscaledTime - _lastRequestTime >= RefreshCooldownSeconds)
                RequestNow();
        }

        private void OnCloseClicked()
        {
            SetOpen(false);
        }

        // ---------------------------------------------------------------- data preparation

        /// <summary>
        /// Who is online and who this player is, so rows can be coloured. Names are matched the way the statistics store them.
        /// A change rebuilds the list (the colours are part of the rows).
        /// </summary>
        private void RefreshPresence()
        {
            _nextPresenceRefresh = Time.unscaledTime + PresenceRefreshSeconds;

            HashSet<string> fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string localName = string.Empty;

            try
            {
                Player local = Player.m_localPlayer;
                localName = (local != null ? ReportStats.NormalizeName(local.GetPlayerName()) : null) ?? string.Empty;

                ZNet znet = ZNet.instance;
                if (znet != null)
                {
                    foreach (ZNet.PlayerInfo info in znet.GetPlayerList())
                    {
                        string name = ReportStats.NormalizeName(info.m_name);
                        if (name != null)
                            fresh.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // Colouring is cosmetic: without it every row is simply drawn in the normal colour.
            }

            if (fresh.SetEquals(_online) && string.Equals(localName, _localName, StringComparison.Ordinal))
                return;

            _online.Clear();
            foreach (string name in fresh)
                _online.Add(name);

            _localName = localName;
            _listDirty = true;
        }

        private void RebuildRows()
        {
            _rows.Clear();
            _bossRows.Clear();
            _hitRows.Clear();
            _killPlayers.Clear();
            _killerPlayers.Clear();
            _deathPlayers.Clear();
            _bossKillsByPlayer.Clear();
            _topCache.Clear();
            _listDirty = true;

            if (_data == null)
                return;

            _rows.AddRange(_data.Players);

            // "Bosses killed" of a player: the boss kills where that player landed the killing blow.
            if (_data.HasBossDetails)
            {
                // The server counted them over the whole period.
                foreach (ReportPlayer player in _data.Players)
                {
                    if (player.BossesKilled > 0)
                        _bossKillsByPlayer[player.Name] = player.BossesKilled;
                }
            }
            else
            {
                // A server of an older build does not send the counts: work them out from the boss list that came with the reply
                // (the newest 50 boss kills).
                foreach (ReportBossKill kill in _data.BossKills)
                {
                    if (string.IsNullOrEmpty(kill.Killer))
                        continue;

                    int count;
                    _bossKillsByPlayer.TryGetValue(kill.Killer, out count);
                    _bossKillsByPlayer[kill.Killer] = count + 1;
                }
            }

            // The lists behind the Highest hits, Mobs killed, Killer mobs and Death causes tabs: only players who have something to show.
            foreach (ReportPlayer player in _data.Players)
            {
                if (player.HighestHitDamage > 0.0)
                    _hitRows.Add(player);

                if (player.MobKillsByType.Count > 0)
                    _killPlayers.Add(player);

                if (player.DeathsByMob.Count > 0)
                    _killerPlayers.Add(player);

                if (player.DeathCauses.Count > 0)
                    _deathPlayers.Add(player);
            }

            // The Killer mobs tab lists the players who died most first.
            _killerPlayers.Sort(delegate (ReportPlayer a, ReportPlayer b)
            {
                int byDeaths = b.Deaths.CompareTo(a.Deaths);
                return byDeaths != 0 ? byDeaths : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // The Death causes tab too: the players who died most first.
            _deathPlayers.Sort(delegate (ReportPlayer a, ReportPlayer b)
            {
                int byDeaths = b.Deaths.CompareTo(a.Deaths);
                return byDeaths != 0 ? byDeaths : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _hitRows.Sort(delegate (ReportPlayer a, ReportPlayer b)
            {
                int byDamage = b.HighestHitDamage.CompareTo(a.HighestHitDamage);
                return byDamage != 0 ? byDamage : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            int column = _sortColumn;
            bool descending = _sortDesc;

            _rows.Sort(delegate (ReportPlayer a, ReportPlayer b)
            {
                int result;
                switch (column)
                {
                    case ColKills:
                        result = a.MobKills.CompareTo(b.MobKills);
                        break;
                    case ColBosses:
                        result = BossesKilledBy(a.Name).CompareTo(BossesKilledBy(b.Name));
                        break;
                    case ColDeaths:
                        result = a.Deaths.CompareTo(b.Deaths);
                        break;
                    case ColDamage:
                        result = a.Damage.CompareTo(b.Damage);
                        break;
                    default:
                        result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        break;
                }

                if (descending)
                    result = -result;

                if (result != 0)
                    return result;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // Newest boss kill first.
            for (int i = _data.BossKills.Count - 1; i >= 0; i--)
                _bossRows.Add(_data.BossKills[i]);
        }

        private void UpdateHeaderLabels()
        {
            _hdrName = "Player" + SortMark(ColName);
            _hdrKills = "Mobs Killed" + SortMark(ColKills);
            _hdrBosses = "Bosses killed" + SortMark(ColBosses);
            _hdrDeaths = "Deaths" + SortMark(ColDeaths);
            _hdrDamage = "Total Damage" + SortMark(ColDamage);
            _listDirty = true;
        }

        private string SortMark(int column)
        {
            if (column != _sortColumn)
                return string.Empty;

            return _sortDesc ? "  v" : "  ^";
        }

        /// <summary>
        /// The header lines under the title. Rebuilt every second because of the "next report in ..." countdown.
        /// </summary>
        private void RebuildTexts()
        {
            _nextTextRebuild = Time.unscaledTime + 1f;

            ReportWindowData data = _data;
            if (data == null)
            {
                _subtitleText = string.Empty;
                _nextReportText = string.Empty;
                return;
            }

            string server = string.IsNullOrEmpty(data.ServerName) ? "Valheim Server" : data.ServerName;
            string range = data.StartUtc.ToLocalTime().ToString("d MMM", Inv) + " - " +
                           data.EndUtc.ToLocalTime().ToString("d MMM yyyy", Inv);

            _subtitleText = server + "   |   " + range;

            TimeSpan left = data.NextDueUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                _nextReportText = "Next report: any moment now";
            }
            else
            {
                _nextReportText = "Next report: " +
                                  data.NextDueUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", Inv) +
                                  " your time (in " + FormatSpan(left) + ")";
            }
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1.0)
                return ((int)span.TotalDays).ToString(Inv) + "d " + span.Hours.ToString(Inv) + "h";

            if (span.TotalHours >= 1.0)
                return span.Hours.ToString(Inv) + "h " + span.Minutes.ToString(Inv) + "m";

            return Math.Max(1, span.Minutes).ToString(Inv) + "m";
        }

        private static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
                return "?";

            if (text.Length <= maxChars)
                return text;

            return text.Substring(0, maxChars - 3) + "...";
        }

        // ---------------------------------------------------------------- window placement

        /// <summary>
        /// Opening: the size is the one the player left the window at (or the compact default), and never less than the content
        /// needs; the position is remembered between openings (centred the first time).
        /// </summary>
        private void PositionWindow()
        {
            LoadPreferredSize();

            float scale = GetScale();
            float availableWidth = Screen.width / scale;
            float availableHeight = Screen.height / scale;

            _resizing = false;
            _dragging = false;
            _lastHeight = 0f;

            // Size first, so the window can be centred with its real size.
            ClampRect(scale);

            if (!_rectPlaced)
            {
                _rect.x = (availableWidth - _rect.width) * 0.5f;
                _rect.y = (availableHeight - _rect.height) * 0.5f;
                _rectPlaced = true;
            }

            ClampRect(scale);
        }

        /// <summary>
        /// Reads the size the player left the window at last time. 0 (never resized) means the default.
        /// </summary>
        private void LoadPreferredSize()
        {
            float width = ReportConfig.WindowWidth != null ? ReportConfig.WindowWidth.Value : 0f;
            float height = ReportConfig.WindowHeight != null ? ReportConfig.WindowHeight.Value : 0f;

            _prefWidth = width >= 1f ? width : DefaultWidth;
            _prefHeight = height >= 1f ? height : DefaultHeight;
        }

        /// <summary>
        /// Saves the size the player gave the window, so it is the same next time (also after restarting the game).
        /// </summary>
        private void SavePreferredSize()
        {
            try
            {
                ConfigEntry<float> widthEntry = ReportConfig.WindowWidth;
                ConfigEntry<float> heightEntry = ReportConfig.WindowHeight;
                if (widthEntry == null || heightEntry == null)
                    return;

                float width = Mathf.Round(_prefWidth);
                float height = Mathf.Round(_prefHeight);

                if (Mathf.Approximately(widthEntry.Value, width) && Mathf.Approximately(heightEntry.Value, height))
                    return;

                // One write to the file for both values.
                ConfigFile file = SharedConfig.File;
                bool saveOnSet = file.SaveOnConfigSet;
                file.SaveOnConfigSet = false;

                try
                {
                    widthEntry.Value = width;
                    heightEntry.Value = height;
                }
                finally
                {
                    file.SaveOnConfigSet = saveOnSet;
                }

                file.Save();
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>
        /// The tabs that have no column header of their own above the list (their tables, if any, carry their own headers), so
        /// their list starts higher.
        /// </summary>
        private bool TabIsRankings()
        {
            return _tab == TabMobsKilled || _tab == TabKillerMobs || _tab == TabDeathCauses;
        }

        /// <summary>
        /// How tall what the current tab shows is, as a list (rows, or blocks of rows). Zero without data.
        /// </summary>
        private float ContentHeight()
        {
            if (_data == null)
                return 0f;

            switch (_tab)
            {
                case TabBosses:
                    return _bossRows.Count * BossRowHeight;

                case TabMobsKilled:
                    return MobsKilledContentHeight();

                case TabKillerMobs:
                    return KillerMobsContentHeight();

                case TabDeathCauses:
                    return DeathCausesContentHeight();

                case TabHighestHits:
                    return _hitRows.Count * RowHeight;

                default:
                    return _rows.Count * RowHeight + (_data.HiddenPlayers > 0 ? RowHeight : 0f);
            }
        }

        /// <summary>
        /// The smallest height at which nothing the current tab shows is cut off: the fixed parts (title, tiles, tabs, column
        /// header, footer) plus the list. A long list scrolls, so it counts for at most MaxRequiredListHeight; a short one
        /// still gets MinListHeight so the list is never squeezed to nothing.
        /// </summary>
        private float RequiredHeight()
        {
            if (_data == null)
                return EmptyBodyTop + EmptyBodyHeight + FooterHeight + 4f + 2f * Frame;

            float top = TabIsRankings() ? HeaderTop : ListTop;
            float list = Mathf.Clamp(ContentHeight(), MinListHeight, MaxRequiredListHeight);
            return top + list + FooterHeight + 4f + 2f * Frame;
        }

        /// <summary>
        /// Applies the chosen size (or the larger one the content needs) and keeps the window on the screen: at least the
        /// title bar stays visible, so the window can always be dragged back.
        /// </summary>
        private void ClampRect(float scale)
        {
            float availableWidth = Screen.width / scale;
            float availableHeight = Screen.height / scale;
            const float minVisible = 80f;

            float maxWidth = Mathf.Max(1f, availableWidth - 24f);
            float maxHeight = Mathf.Max(1f, availableHeight - 24f);
            float minWidth = Mathf.Min(MinWidth, maxWidth);
            float minHeight = Mathf.Min(RequiredHeight(), maxHeight);

            // While a grip is being dragged the drag decides the size; otherwise it is what the player chose.
            if (!_resizing)
            {
                _rect.width = _prefWidth;
                _rect.height = _prefHeight;
            }

            _rect.width = Mathf.Clamp(_rect.width, minWidth, maxWidth);
            _rect.height = Mathf.Clamp(_rect.height, minHeight, maxHeight);

            // A tab whose content needs more room must not push the bottom (and the buttons) off the screen.
            if (!_resizing && _lastHeight > 0f && _rect.height > _lastHeight + 0.5f && _rect.yMax > availableHeight - 12f)
                _rect.y = Mathf.Max(0f, availableHeight - 12f - _rect.height);

            _lastHeight = _rect.height;

            _rect.x = Mathf.Clamp(_rect.x, minVisible - _rect.width, availableWidth - minVisible);
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, availableHeight - 44f));
        }

        private static float GetScale()
        {
            float configured = ReportConfig.WindowScale != null ? ReportConfig.WindowScale.Value : 0f;

            // 0 = automatic: 1.0 at 1080p, bigger on taller screens so the text stays readable.
            if (configured <= 0.01f)
                return Mathf.Clamp(Screen.height / 1080f, 0.85f, 2f);

            return Mathf.Clamp(configured, 0.7f, 2f);
        }

        // ---------------------------------------------------------------- dragging and resizing

        private void OnTitlePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || IsInsideButton(evt.target as VisualElement))
                return;

            _dragging = true;
            _dragStartPointer = evt.position;
            _dragStartRect = _rect;
            _titleBar.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnTitlePointerMove(PointerMoveEvent evt)
        {
            if (!_dragging || !_titleBar.HasPointerCapture(evt.pointerId))
                return;

            Vector2 pointer = evt.position;
            _rect.x = _dragStartRect.x + (pointer.x - _dragStartPointer.x);
            _rect.y = _dragStartRect.y + (pointer.y - _dragStartPointer.y);
            evt.StopPropagation();
        }

        private void OnTitlePointerUp(PointerUpEvent evt)
        {
            if (_titleBar.HasPointerCapture(evt.pointerId))
                _titleBar.ReleasePointer(evt.pointerId);

            _dragging = false;
        }

        private bool IsInsideButton(VisualElement element)
        {
            while (element != null && element != _titleBar)
            {
                if (element is Button)
                    return true;

                element = element.parent;
            }

            return false;
        }

        private void OnGripPointerDown(PointerDownEvent evt)
        {
            VisualElement grip = evt.currentTarget as VisualElement;
            if (evt.button != 0 || grip == null)
                return;

            _resizing = true;
            _resizeStartPointer = evt.position;
            _resizeStartRect = _rect;
            grip.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnGripPointerMove(PointerMoveEvent evt)
        {
            VisualElement grip = evt.currentTarget as VisualElement;
            if (!_resizing || grip == null || !grip.HasPointerCapture(evt.pointerId))
                return;

            // Pointer positions are in panel units, which are the units the window rectangle is in: no scaling needed.
            Vector2 pointer = evt.position;
            float dx = pointer.x - _resizeStartPointer.x;
            float dy = pointer.y - _resizeStartPointer.y;

            float scale = Mathf.Max(0.01f, GetScale());
            float availableWidth = Screen.width / scale;
            float availableHeight = Screen.height / scale;

            // Never smaller than the content needs, never bigger than the screen.
            float minWidth = Mathf.Min(MinWidth, Mathf.Max(1f, availableWidth - 24f));
            float minHeight = RequiredHeight();
            float maxWidth = Mathf.Max(minWidth, availableWidth - _resizeStartRect.x - 12f);
            float maxHeight = Mathf.Max(minHeight, availableHeight - _resizeStartRect.y - 12f);

            _rect.width = Mathf.Clamp(_resizeStartRect.width + dx, minWidth, maxWidth);
            _rect.height = Mathf.Clamp(_resizeStartRect.height + dy, minHeight, maxHeight);
            evt.StopPropagation();
        }

        private void OnGripPointerUp(PointerUpEvent evt)
        {
            VisualElement grip = evt.currentTarget as VisualElement;
            if (grip != null && grip.HasPointerCapture(evt.pointerId))
                grip.ReleasePointer(evt.pointerId);

            FinishResize();
        }

        private void FinishResize()
        {
            if (!_resizing)
                return;

            _resizing = false;

            // Only a real resize is remembered: a click on a grip that moved nothing must not turn a temporary
            // enlargement (more content than usual) into the player's chosen size.
            if (Mathf.Abs(_rect.width - _resizeStartRect.width) > 0.5f ||
                Mathf.Abs(_rect.height - _resizeStartRect.height) > 0.5f)
            {
                _prefWidth = _rect.width;
                _prefHeight = _rect.height;
                SavePreferredSize();
            }
        }

        // ---------------------------------------------------------------- per-frame UI update

        private void UpdateUi()
        {
            if (_window == null)
                return;

            float scale = GetScale();
            if (!Mathf.Approximately(scale, _appliedScale))
            {
                _panelSettings.scale = scale;
                _appliedScale = scale;
            }

            ClampRect(scale);
            ApplyRect();
            UpdateChrome();

            if (_listDirty)
                RebuildList();
        }

        private void ApplyRect()
        {
            IStyle s = _window.style;
            s.left = _rect.x;
            s.top = _rect.y;
            s.width = _rect.width;
            s.height = _rect.height;
        }

        /// <summary>
        /// Everything that is cheap to set again every frame: texts, which body is shown, button states.
        /// </summary>
        private void UpdateChrome()
        {
            bool hasData = _data != null;

            SetDisplay(_emptyBody, !hasData);
            SetDisplay(_reportBody, hasData);

            if (!hasData)
            {
                _emptyLabel.text = _awaiting
                    ? "Loading the weekly report..."
                    : (string.IsNullOrEmpty(_message) ? "No data yet." : _message);
            }
            else
            {
                _subtitleLabel.text = _subtitleText;
                _nextReportLabel.text = _nextReportText;

                _tileValues[0].text = _tileJoined;
                _tileValues[1].text = _tileKills;
                _tileValues[2].text = _tileDeaths;
                _tileValues[3].text = _tileBosses;
                _tileValues[4].text = _tileTopValue;
                _tileCaptions[4].text = _tileTopCaption;

                _tabButtons[TabPlayers].Element.text = _tabPlayersLabel;
                _tabButtons[TabBosses].Element.text = _tabBossesLabel;

                for (int i = 0; i < TabCount; i++)
                {
                    bool active = _tab == i;
                    if (_tabStyledActive[i] != active)
                    {
                        _tabButtons[i].Element.EnableInClassList("vdr-tab-active", active);
                        _tabStyledActive[i] = active;
                        SkinElement(_tabButtons[i].Element);
                    }
                }

                _legendLabel.text = _tab == TabPlayers ? "Green = online    Gold = you"
                    : (_tab == TabBosses ? "Kills = players this boss killed    Gold = you"
                    : (_tab == TabMobsKilled ? "Highest hit = biggest single hit on that kind of mob    Gold = you"
                    : (_tab == TabKillerMobs ? "Most deaths by whom = who died to that mob most (bosses included)    Gold = you"
                    : (_tab == TabDeathCauses ? "Finishing hit/damage = the hit that killed the player (the biggest one per cause)    Gold = you" : string.Empty))));
            }

            // Footer: when something went wrong with the last refresh say so instead of the usual two lines.
            bool showWarn = hasData && !string.IsNullOrEmpty(_message) && !_awaiting;
            SetDisplay(_warnLabel, showWarn);
            SetDisplay(_footerInfo, !showWarn);

            if (showWarn)
            {
                _warnLabel.text = _message;
            }
            else
            {
                bool optedOut = ReportConfig.ClientEnabled != null && !ReportConfig.ClientEnabled.Value;

                _statusLabel.text = _awaiting ? "Refreshing..." : _updatedText;
                _hintLabel.text = optedOut
                    ? "You opted out of the report: your kills and deaths are not counted."
                    : "Mob kills and damage are counted for players running the mod.";

                if (_keysRow != null)
                {
                    // Modern style: key hints instead of the standing note; the opt-out note still replaces them.
                    SetDisplay(_hintLabel, optedOut);
                    SetDisplay(_keysRow, !optedOut);
                    UpdateToggleKeyHint();
                }
            }

            if (_refreshButton.SetEnabled(!_awaiting && Time.unscaledTime - _lastRequestTime >= RefreshCooldownSeconds))
                SkinElement(_refreshButton.Element);
        }

        /// <summary>Modern style: shows the key that opens and closes the window (hidden when it is set to None).</summary>
        private void UpdateToggleKeyHint()
        {
            if (_toggleKeyLabel == null || _toggleKeyGroup == null)
                return;

            KeyboardShortcut shortcut = ReportConfig.WindowKey != null ? ReportConfig.WindowKey.Value : KeyboardShortcut.Empty;
            bool hasKey = shortcut.MainKey != KeyCode.None;

            SetDisplay(_toggleKeyGroup, hasKey);
            if (hasKey)
                _toggleKeyLabel.text = shortcut.ToString();
        }

        private static void SetDisplay(VisualElement element, bool visible)
        {
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---------------------------------------------------------------- creating the panel

        /// <summary>
        /// Creates the runtime panel and the window's element tree once. False (after a log line) when that failed.
        /// </summary>
        private bool EnsureUi()
        {
            if (_window != null && _document != null)
                return true;

            try
            {
                BuildUi();
                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not create the weekly report window (UI Toolkit).", ex);
                DestroyUi();
                return false;
            }
        }

        /// <summary>The look the player chose (Classic when the setting is not available yet).</summary>
        private static ReportWindowStyle CurrentStyle()
        {
            return ReportConfig.WindowStyle != null ? ReportConfig.WindowStyle.Value : ReportWindowStyle.Classic;
        }

        private void BuildUi()
        {
            _builtStyle = CurrentStyle();
            _modern = _builtStyle == ReportWindowStyle.Modern;

            _font = LoadFont();

            // A runtime panel needs a PanelSettings. It has no theme style sheet (there is none to load without an asset
            // bundle); the look comes from ReportStyleSheet, so the theme is not needed. Unity may log a warning about the
            // missing theme once; it is harmless here.
            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelSettings.hideFlags = HideFlags.HideAndDontSave;
            _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _appliedScale = GetScale();
            _panelSettings.scale = _appliedScale;
            _panelSettings.sortingOrder = PanelSortingOrder;
            _panelSettings.clearColor = false;
            _panelSettings.targetDisplay = 0;

            _uiObject = new GameObject("ValheimDiscordRelay.ReportWindow");
            _uiObject.transform.SetParent(transform, false);

            _document = _uiObject.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;

            VisualElement root = _document.rootVisualElement;
            if (root == null)
                throw new InvalidOperationException("The UI document has no root element.");

            // The root covers the screen: it must never take clicks away from the game.
            root.pickingMode = PickingMode.Ignore;

            _window = CreateWindowTree();
            root.Add(_window);

            LoadStyles();
            SkinTree(_window);

            RelayDiagnostics.Info("[Report] The weekly report window uses UI Toolkit (" + _builtStyle + " style, font: " +
                                  (_font != null ? _font.name : "Unity default") + ", " +
                                  (_styles != null ? _styles.RuleCount.ToString(Inv) + " style rules" : "emergency colours") + ").");
        }

        private void DestroyUi()
        {
            if (_uiObject != null)
                Destroy(_uiObject);

            if (_panelSettings != null)
                Destroy(_panelSettings);

            _uiObject = null;
            _panelSettings = null;
            _document = null;
            _window = null;
            _titleBar = null;
            _keysRow = null;
            _toggleKeyGroup = null;
            _toggleKeyLabel = null;
            _scroll = null;
            _styles = null;
            _styleStamp = DateTime.MinValue;
            _builtTab = -1;
            _appliedScale = -1f;
            Array.Clear(_tabStyledActive, 0, _tabStyledActive.Length);
        }

        /// <summary>
        /// The font the IMGUI window used (Unity's built-in one), so the text looks the same. Null lets UI Toolkit pick its own.
        /// </summary>
        private static Font LoadFont()
        {
            Font font = null;

            try
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                // Tried the next source below.
            }

            if (font == null)
            {
                try
                {
                    font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans" }, 16);
                }
                catch (Exception)
                {
                    // Falls back to UI Toolkit's default font.
                }
            }

            return font;
        }

        // ---------------------------------------------------------------- style sheet

        /// <summary>Where the player's style sheet lives: next to the BepInEx config file. Null when that is unknown.</summary>
        private string StylePath()
        {
            string configFile = SharedConfig.File != null ? SharedConfig.File.ConfigFilePath : null;
            string directory = string.IsNullOrEmpty(configFile) ? null : Path.GetDirectoryName(configFile);

            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, ReportDefaultStyle.FileNameFor(_builtStyle));
        }

        /// <summary>
        /// Reads the built-in style sheet, then the player's file on top of it (written first, with the default rules, when it
        /// does not exist yet). A file that cannot be read is skipped; the built-in look still applies.
        /// </summary>
        private void LoadStyles()
        {
            ReportStyleSheet sheet = new ReportStyleSheet();
            _styleStamp = DateTime.MinValue;

            // Look for Valheim's fonts again (the style sheet may ask for them by name).
            ReportFonts.Reset();

            try
            {
                sheet.AddSource(ReportDefaultStyle.BuiltInText(_builtStyle), "(built in, " + _builtStyle + ")");
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] The built-in style sheet could not be read.", ex);
            }

            string path = StylePath();
            if (path != null)
            {
                try
                {
                    if (!File.Exists(path))
                        File.WriteAllText(path, ReportDefaultStyle.FileTextFor(_builtStyle), new UTF8Encoding(false));

                    if (File.Exists(path))
                    {
                        _styleStamp = File.GetLastWriteTimeUtc(path);
                        sheet.AddSource(File.ReadAllText(path, Encoding.UTF8), ReportDefaultStyle.FileNameFor(_builtStyle));
                    }
                }
                catch (Exception ex)
                {
                    RelayDiagnostics.Warning("[Report] The style sheet " + path + " could not be used: " + ex.Message);
                }
            }

            _styles = sheet.RuleCount > 0 ? sheet : null;
        }

        /// <summary>
        /// Called when the window opens: if the player edited (or deleted) the style sheet file since it was read, read it
        /// again and restyle the window, so changes show without restarting the game.
        /// </summary>
        private void ReloadStylesIfChanged()
        {
            if (_window == null)
                return;

            try
            {
                string path = StylePath();
                if (path == null)
                    return;

                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (stamp == _styleStamp)
                    return;
            }
            catch (Exception)
            {
                return;
            }

            LoadStyles();
            SkinTree(_window);
            _listDirty = true;
        }

        /// <summary>Applies the style sheet to an element and everything below it (new rows, a new header ...).</summary>
        private void SkinTree(VisualElement root)
        {
            if (root == null)
                return;

            if (_styles == null)
            {
                EmergencySkin(root);
                return;
            }

            try
            {
                _styles.Apply(root);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>Applies the style sheet to one element again, after a class or its enabled state changed.</summary>
        private void SkinElement(VisualElement element)
        {
            if (_styles == null)
                return;

            try
            {
                _styles.Refresh(element);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>
        /// Only used when no style rule could be read at all (which should not happen: the built-in sheet is part of the
        /// mod): readable colours, so the window is not black text on a clear background.
        /// </summary>
        private static void EmergencySkin(VisualElement element)
        {
            if (element.ClassListContains("vdr-window"))
            {
                element.style.backgroundColor = new Color(0.075f, 0.062f, 0.050f, 0.97f);
                element.style.borderLeftColor = new Color(0.55f, 0.42f, 0.20f, 1f);
                element.style.borderRightColor = new Color(0.55f, 0.42f, 0.20f, 1f);
                element.style.borderTopColor = new Color(0.55f, 0.42f, 0.20f, 1f);
                element.style.borderBottomColor = new Color(0.55f, 0.42f, 0.20f, 1f);
            }

            if (element is Label || element is Button)
                element.style.color = new Color(0.93f, 0.90f, 0.84f, 1f);

            if (element is Button)
                element.style.backgroundColor = new Color(0.22f, 0.17f, 0.11f, 1f);

            foreach (VisualElement child in element.Children())
                EmergencySkin(child);
        }

        // ---------------------------------------------------------------- the element tree

        private VisualElement CreateWindowTree()
        {
            VisualElement window = new VisualElement();
            window.name = "vdr-report-window";
            window.AddToClassList("vdr-window");
            ResetBox(window);
            SetBorderWidth(window, Frame);
            window.style.position = Position.Absolute;
            window.style.flexDirection = FlexDirection.Column;
            window.style.overflow = Overflow.Hidden;
            window.style.display = DisplayStyle.None;

            // Text style properties are inherited, so setting the font once here covers every label and button.
            if (_font != null)
                window.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(_font));

            window.Add(CreateTitleBar());

            VisualElement titleLine = CreateDivider("vdr-divider");
            if (_modern)
                AddDiamond(titleLine);
            window.Add(titleLine);

            VisualElement bodyHost = new VisualElement();
            ResetBox(bodyHost);
            bodyHost.style.flexGrow = 1f;
            bodyHost.style.flexShrink = 1f;
            bodyHost.style.minHeight = 0f;
            bodyHost.style.marginBottom = 4f;
            bodyHost.style.flexDirection = FlexDirection.Column;
            bodyHost.Add(CreateEmptyBody());
            bodyHost.Add(CreateReportBody());
            window.Add(bodyHost);

            window.Add(CreateFooter());

            // The resize grips are the last children, so they sit on top. Dragging starts on the right or bottom edge or
            // on the corner mark.
            window.Add(CreateGrip(GripRightEdge));
            window.Add(CreateGrip(GripBottomEdge));
            window.Add(CreateGrip(GripCorner));

            return window;
        }

        private VisualElement CreateTitleBar()
        {
            VisualElement bar = new VisualElement();
            bar.AddToClassList("vdr-title-bar");
            ResetBox(bar);
            bar.style.height = TitleBarHeight;
            bar.style.flexShrink = 0f;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = Pad;
            bar.style.paddingRight = Pad;
            bar.style.paddingTop = 3f;

            if (_modern)
                bar.Add(CreateBadge());

            Label title = MakeLabel("WEEKLY REPORT", "vdr-title");
            title.style.flexGrow = 1f;
            title.style.height = 30f;
            bar.Add(title);

            Btn close = new Btn(delegate { Guard(OnCloseClicked); }, "vdr-btn", "vdr-btn-close");
            close.Element.text = "X";
            close.Element.style.width = 28f;
            close.Element.style.height = 26f;
            close.Element.style.flexShrink = 0f;
            bar.Add(close.Element);

            // Only the title bar drags the window (buttons on it keep their clicks, see IsInsideButton).
            bar.RegisterCallback<PointerDownEvent>(OnTitlePointerDown);
            bar.RegisterCallback<PointerMoveEvent>(OnTitlePointerMove);
            bar.RegisterCallback<PointerUpEvent>(OnTitlePointerUp);
            bar.RegisterCallback<PointerCaptureOutEvent>(delegate { _dragging = false; });

            _titleBar = bar;
            return bar;
        }

        private static VisualElement CreateDivider(string cssClass)
        {
            VisualElement line = new VisualElement();
            line.AddToClassList(cssClass);
            ResetBox(line);
            line.style.height = 1f;
            line.style.flexShrink = 0f;
            line.style.marginLeft = Pad;
            line.style.marginRight = Pad;
            line.pickingMode = PickingMode.Ignore;
            return line;
        }

        /// <summary>Modern style: a small gold diamond in the middle of a divider line (a rotated square).</summary>
        private static void AddDiamond(VisualElement line)
        {
            VisualElement diamond = new VisualElement();
            diamond.AddToClassList("vdr-diamond");
            ResetBox(diamond);
            diamond.style.position = Position.Absolute;
            diamond.style.left = Percent(50f);
            diamond.style.marginLeft = -3f;
            diamond.style.top = -2.5f;
            diamond.style.width = 6f;
            diamond.style.height = 6f;
            diamond.style.rotate = new StyleRotate(new Rotate(45f));
            diamond.pickingMode = PickingMode.Ignore;
            line.Add(diamond);
        }

        /// <summary>
        /// Modern style: the round icon at the left of the title, with three bars (a tiny chart) drawn from plain elements, so no
        /// image or icon font is needed.
        /// </summary>
        private static VisualElement CreateBadge()
        {
            VisualElement badge = new VisualElement();
            badge.AddToClassList("vdr-badge");
            ResetBox(badge);
            SetBorderWidth(badge, 1f);
            badge.style.width = 28f;
            badge.style.height = 28f;
            badge.style.flexShrink = 0f;
            badge.style.marginRight = 10f;
            badge.pickingMode = PickingMode.Ignore;

            // Inside the 1 px frame the badge is 26 x 26; the bars sit on a baseline 6 px above its bottom edge.
            float[] heights = { 6f, 10f, 14f };
            for (int i = 0; i < heights.Length; i++)
            {
                VisualElement bar = new VisualElement();
                bar.AddToClassList("vdr-badge-bar");
                ResetBox(bar);
                bar.style.position = Position.Absolute;
                bar.style.left = 6f + i * 5f;
                bar.style.width = 3f;
                bar.style.height = heights[i];
                bar.style.top = 20f - heights[i];
                bar.pickingMode = PickingMode.Ignore;
                badge.Add(bar);
            }

            return badge;
        }

        private VisualElement CreateEmptyBody()
        {
            VisualElement body = new VisualElement();
            ResetBox(body);
            body.style.marginTop = EmptyBodyTop - TitleBarHeight - 1f;
            body.style.marginLeft = Pad + 30f;
            body.style.marginRight = Pad + 30f;
            body.style.height = EmptyBodyHeight;
            body.style.flexShrink = 0f;
            body.style.flexDirection = FlexDirection.Column;

            Label label = MakeLabel(string.Empty, "vdr-empty-text");
            label.style.flexGrow = 1f;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.textOverflow = TextOverflow.Clip;
            body.Add(label);

            _emptyLabel = label;
            _emptyBody = body;
            return body;
        }

        private VisualElement CreateReportBody()
        {
            VisualElement body = new VisualElement();
            ResetBox(body);
            body.style.flexGrow = 1f;
            body.style.flexShrink = 1f;
            body.style.minHeight = 0f;
            body.style.paddingLeft = Pad;
            body.style.paddingRight = Pad;
            body.style.flexDirection = FlexDirection.Column;
            body.style.display = DisplayStyle.None;

            _subtitleLabel = MakeLabel(string.Empty, "vdr-subtitle");
            _subtitleLabel.style.marginTop = SubtitleGap;
            _subtitleLabel.style.height = SubtitleHeight;
            body.Add(_subtitleLabel);

            _nextReportLabel = MakeLabel(string.Empty, "vdr-subtitle");
            _nextReportLabel.style.height = SubtitleHeight;
            body.Add(_nextReportLabel);

            body.Add(CreateTiles());
            body.Add(CreateTabs());

            _legendLabel = MakeLabel(string.Empty, "vdr-legend");
            _legendLabel.style.marginTop = LegendGap;
            _legendLabel.style.height = LegendHeight;
            body.Add(_legendLabel);

            // Column header (rebuilt with the list) and the scrolling list under it.
            _tableHeader = new VisualElement();
            _tableHeader.AddToClassList("vdr-table-header");
            ResetBox(_tableHeader);
            _tableHeader.style.marginTop = HeaderGap;
            _tableHeader.style.height = HeaderHeight;
            _tableHeader.style.flexShrink = 0f;
            _tableHeader.style.flexDirection = FlexDirection.Row;
            _tableHeader.style.paddingRight = ScrollbarWidth;
            body.Add(_tableHeader);

            _listHost = new VisualElement();
            ResetBox(_listHost);
            _listHost.style.flexGrow = 1f;
            _listHost.style.flexShrink = 1f;
            _listHost.style.minHeight = 0f;

            _scroll = new ScrollPane();
            FillParent(_scroll);
            _listHost.Add(_scroll);

            _listMessage = MakeLabel(string.Empty, "vdr-list-message");
            FillParent(_listMessage);
            _listMessage.style.whiteSpace = WhiteSpace.Normal;
            _listMessage.style.textOverflow = TextOverflow.Clip;
            _listMessage.style.display = DisplayStyle.None;
            _listHost.Add(_listMessage);

            body.Add(_listHost);

            _reportBody = body;
            return body;
        }

        private VisualElement CreateTiles()
        {
            VisualElement row = new VisualElement();
            ResetBox(row);
            row.style.marginTop = TilesGap;
            row.style.height = TileHeight;
            row.style.flexShrink = 0f;
            row.style.flexDirection = FlexDirection.Row;

            string[] captions = { "Players joined", "Mob kills", "Deaths", "Bosses defeated", "Top damage" };

            for (int i = 0; i < 5; i++)
            {
                VisualElement tile = new VisualElement();
                tile.AddToClassList("vdr-tile");
                ResetBox(tile);
                tile.style.height = TileHeight;
                tile.style.flexDirection = FlexDirection.Column;

                // The last tile is wider: it carries a player name.
                if (i < 4)
                {
                    tile.style.width = TileSmall;
                    tile.style.flexShrink = 0f;
                    tile.style.marginRight = TileGap;
                }
                else
                {
                    tile.style.flexGrow = 1f;
                    tile.style.flexShrink = 1f;
                    tile.style.minWidth = 0f;
                }

                Label value = i == 2
                    ? MakeLabel("0", "vdr-tile-value", "vdr-tile-value-death")
                    : MakeLabel("0", "vdr-tile-value");
                value.style.marginTop = 6f;
                value.style.height = 28f;
                tile.Add(value);

                Label caption = MakeLabel(captions[i], "vdr-tile-caption");
                caption.style.marginTop = 1f;
                caption.style.height = 18f;
                tile.Add(caption);

                _tileValues[i] = value;
                _tileCaptions[i] = caption;
                row.Add(tile);
            }

            return row;
        }

        private VisualElement CreateTabs()
        {
            VisualElement holder = new VisualElement();
            ResetBox(holder);
            holder.style.flexShrink = 0f;
            holder.style.flexDirection = FlexDirection.Column;

            // Two rows of three tabs.
            VisualElement rowOne = MakeTabRow(TabsGap);
            AddTab(rowOne, TabPlayers, "Players", true);
            AddTab(rowOne, TabBosses, "Bosses", true);
            AddTab(rowOne, TabMobsKilled, "Mobs killed", false);
            holder.Add(rowOne);

            VisualElement rowTwo = MakeTabRow(TabRowGap);
            AddTab(rowTwo, TabKillerMobs, "Killer mobs", true);
            AddTab(rowTwo, TabDeathCauses, "Death causes", true);
            AddTab(rowTwo, TabHighestHits, "Highest hits", false);
            holder.Add(rowTwo);

            return holder;
        }

        private static VisualElement MakeTabRow(float marginTop)
        {
            VisualElement row = new VisualElement();
            ResetBox(row);
            row.style.marginTop = marginTop;
            row.style.height = TabHeight;
            row.style.flexShrink = 0f;
            row.style.flexDirection = FlexDirection.Row;
            return row;
        }

        private void AddTab(VisualElement row, int tab, string text, bool gapAfter)
        {
            Btn button = new Btn(delegate { Guard(delegate { OnTabClicked(tab); }); }, "vdr-tab");
            button.Element.text = text;
            button.Element.style.flexGrow = 1f;
            button.Element.style.flexShrink = 1f;
            button.Element.style.flexBasis = 0f;
            button.Element.style.minWidth = 0f;

            if (gapAfter)
                button.Element.style.marginRight = TileGap;

            _tabButtons[tab] = button;
            row.Add(button.Element);
        }

        private VisualElement CreateFooter()
        {
            VisualElement footer = new VisualElement();
            ResetBox(footer);
            footer.style.height = FooterHeight;
            footer.style.flexShrink = 0f;
            footer.style.flexDirection = FlexDirection.Column;
            footer.style.paddingLeft = Pad;
            footer.style.paddingRight = Pad;

            // The footer's own padding already insets the line, so it needs no margins of its own.
            VisualElement line = CreateDivider("vdr-footer-line");
            line.style.marginLeft = 0f;
            line.style.marginRight = 0f;
            footer.Add(line);

            VisualElement row = new VisualElement();
            ResetBox(row);
            row.style.flexGrow = 1f;
            row.style.flexDirection = FlexDirection.Row;

            VisualElement left = new VisualElement();
            ResetBox(left);
            left.style.flexGrow = 1f;
            left.style.flexShrink = 1f;
            left.style.minWidth = 0f;
            left.style.marginRight = 12f;
            left.style.flexDirection = FlexDirection.Column;

            _footerInfo = new VisualElement();
            ResetBox(_footerInfo);
            _footerInfo.style.flexDirection = FlexDirection.Column;

            _statusLabel = MakeLabel(string.Empty, "vdr-status");
            _statusLabel.style.marginTop = 7f;
            _statusLabel.style.height = 18f;
            _footerInfo.Add(_statusLabel);

            _hintLabel = MakeLabel(string.Empty, "vdr-hint");
            _hintLabel.style.marginTop = 2f;
            _hintLabel.style.height = 18f;
            _footerInfo.Add(_hintLabel);

            left.Add(_footerInfo);

            if (_modern)
            {
                // Same place and height as the hint line. The hint line only shows in this style when it has something to say
                // (the note that you opted out); otherwise the key hints are shown.
                _keysRow = new VisualElement();
                ResetBox(_keysRow);
                _keysRow.style.flexDirection = FlexDirection.Row;
                _keysRow.style.alignItems = Align.Center;
                _keysRow.style.marginTop = 2f;
                _keysRow.style.height = 18f;

                AddKeyHint(_keysRow, "Drag", "to move", false);
                AddKeyHint(_keysRow, "Esc", "to close", false);
                _toggleKeyGroup = AddKeyHint(_keysRow, string.Empty, "to toggle", true);
                _footerInfo.Add(_keysRow);
            }

            _warnLabel = MakeLabel(string.Empty, "vdr-warn");
            _warnLabel.style.marginTop = 4f;
            _warnLabel.style.height = 46f;
            _warnLabel.style.whiteSpace = WhiteSpace.Normal;
            _warnLabel.style.textOverflow = TextOverflow.Clip;
            _warnLabel.style.display = DisplayStyle.None;
            left.Add(_warnLabel);

            row.Add(left);

            VisualElement buttons = new VisualElement();
            ResetBox(buttons);
            buttons.style.flexShrink = 0f;
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.alignItems = Align.FlexStart;
            buttons.style.marginTop = 11f;

            _refreshButton = MakeFooterButton("Refresh", OnRefreshClicked, "vdr-btn-primary");
            _refreshButton.Element.style.marginRight = 8f;
            buttons.Add(_refreshButton.Element);

            Btn closeButton = MakeFooterButton("Close", OnCloseClicked);
            buttons.Add(closeButton.Element);

            row.Add(buttons);
            footer.Add(row);
            return footer;
        }

        /// <summary>
        /// Modern style: a key cap (the key's name in a small rounded box) followed by what it does. Returns the group so the
        /// caller can hide it; when <paramref name="keyIsConfigured"/> is true the key cap's text is set every frame from the
        /// window key setting (see UpdateChrome).
        /// </summary>
        private VisualElement AddKeyHint(VisualElement row, string key, string text, bool keyIsConfigured)
        {
            VisualElement group = new VisualElement();
            ResetBox(group);
            group.style.flexDirection = FlexDirection.Row;
            group.style.alignItems = Align.Center;
            group.style.flexShrink = 0f;
            group.style.marginRight = 12f;

            Label cap = MakeLabel(key, "vdr-key");
            cap.style.height = 16f;
            group.Add(cap);

            Label caption = MakeLabel(text, "vdr-key-text");
            caption.style.marginLeft = 5f;
            caption.style.height = 16f;
            group.Add(caption);

            if (keyIsConfigured)
                _toggleKeyLabel = cap;

            row.Add(group);
            return group;
        }

        private Btn MakeFooterButton(string text, Action onClick, params string[] extraClasses)
        {
            Btn button = new Btn(delegate { Guard(onClick); }, "vdr-btn", "vdr-btn-footer");

            foreach (string cssClass in extraClasses)
                button.Element.AddToClassList(cssClass);

            // The rune font of the Modern style reads best in capitals.
            button.Element.text = _modern ? text.ToUpperInvariant() : text;
            button.Element.style.width = ButtonWidth;
            button.Element.style.height = 30f;
            button.Element.style.flexShrink = 0f;
            return button;
        }

        private VisualElement CreateGrip(int kind)
        {
            VisualElement grip = new VisualElement();
            ResetBox(grip);
            grip.style.position = Position.Absolute;

            if (kind == GripCorner)
            {
                // The corner, with its mark.
                grip.style.right = 0f;
                grip.style.bottom = 0f;
                grip.style.width = ResizeGrip;
                grip.style.height = ResizeGrip;
                grip.Add(MakeMark(5f, 10f, 6f, 1f));
                grip.Add(MakeMark(9f, 6f, 1f, 6f));
                grip.Add(MakeMark(4f, 12f, 7f, 1f));
            }
            else if (kind == GripBottomEdge)
            {
                grip.style.left = 0f;
                grip.style.right = ResizeGrip;
                grip.style.bottom = 0f;
                grip.style.height = ResizeEdge;
            }
            else
            {
                // The right edge, below the title bar.
                grip.style.top = TitleBarHeight;
                grip.style.right = 0f;
                grip.style.bottom = ResizeGrip;
                grip.style.width = ResizeEdge;
            }

            grip.RegisterCallback<PointerDownEvent>(OnGripPointerDown);
            grip.RegisterCallback<PointerMoveEvent>(OnGripPointerMove);
            grip.RegisterCallback<PointerUpEvent>(OnGripPointerUp);
            grip.RegisterCallback<PointerCaptureOutEvent>(delegate { FinishResize(); });
            return grip;
        }

        private static VisualElement MakeMark(float left, float top, float width, float height)
        {
            VisualElement mark = new VisualElement();
            mark.AddToClassList("vdr-grip-mark");
            ResetBox(mark);
            mark.style.position = Position.Absolute;
            mark.style.left = left;
            mark.style.top = top;
            mark.style.width = width;
            mark.style.height = height;
            mark.pickingMode = PickingMode.Ignore;
            return mark;
        }

        // ---------------------------------------------------------------- the list under the tabs

        /// <summary>
        /// Rebuilds the column header and the rows of the current tab. Runs only when something it shows has changed.
        /// </summary>
        private void RebuildList()
        {
            _listDirty = false;

            if (_builtTab >= 0)
                _scrollOffsets[_builtTab] = _scroll.Offset;

            _builtTab = _tab;

            _tableHeader.Clear();
            _scroll.Content.Clear();

            bool rankings = TabIsRankings();
            SetDisplay(_tableHeader, !rankings);
            _listHost.style.marginTop = rankings ? HeaderGap : 0f;

            bool hasRows = false;
            string emptyText = string.Empty;

            if (_data != null)
            {
                switch (_tab)
                {
                    case TabBosses:
                        hasRows = BuildBossesTab();
                        emptyText = "No bosses have been defeated this week.";
                        break;

                    case TabMobsKilled:
                        hasRows = BuildMobsKilled();
                        emptyText = "No mobs have been killed this week.";
                        break;

                    case TabKillerMobs:
                        hasRows = BuildKillerMobs();
                        emptyText = "No player has been killed by a mob this week.";
                        break;

                    case TabDeathCauses:
                        hasRows = BuildDeathCauses();
                        emptyText = "No player has died this week.";
                        break;

                    case TabHighestHits:
                        hasRows = BuildHighestHits();
                        emptyText = "No single-hit damage has been recorded this week.";
                        break;

                    default:
                        hasRows = BuildPlayersTab();
                        emptyText = "No players have been active this week.";
                        break;
                }
            }

            SetDisplay(_scroll, hasRows);
            SetDisplay(_listMessage, !hasRows && _data != null);
            _listMessage.text = emptyText;
            _scroll.Offset = _scrollOffsets[_tab];

            // The new elements get their look from the style sheet.
            SkinTree(_tableHeader);
            SkinTree(_scroll.Content);
        }

        private bool BuildPlayersTab()
        {
            // Column headers: click to sort.
            _tableHeader.Add(MakeSortHeader(_hdrName, ColName, false, 0f));
            _tableHeader.Add(MakeSortHeader(_hdrKills, ColKills, true, ColMobsWidth));
            _tableHeader.Add(MakeSortHeader(_hdrBosses, ColBosses, true, ColBossesWidth));
            _tableHeader.Add(MakeSortHeader(_hdrDeaths, ColDeaths, true, ColDeathsWidth));
            _tableHeader.Add(MakeSortHeader(_hdrDamage, ColDamage, true, ColDamageWidth));

            int count = _rows.Count;
            if (count == 0)
                return false;

            for (int i = 0; i < count; i++)
                _scroll.Content.Add(MakePlayerRow(i, _rows[i]));

            if (_data != null && _data.HiddenPlayers > 0)
            {
                VisualElement more = MakeRow(null);
                more.Add(RowCell("...and " + _data.HiddenPlayers.ToString("N0", Inv) + " more not shown",
                    "vdr-cell", false, 0f, false, null));
                _scroll.Content.Add(more);
            }

            return true;
        }

        private VisualElement MakePlayerRow(int index, ReportPlayer player)
        {
            bool isYou = IsYou(player.Name);
            bool isOnline = _online.Contains(player.Name);

            // As tall as its tallest cell: a long name wraps onto a second line instead of being cut off.
            VisualElement row = MakeFlexRow(isYou ? "vdr-row-you" : ((index & 1) == 0 ? "vdr-row-alt" : null));

            string label = isYou ? player.Name + "  (you)" : player.Name;
            string nameState = isYou ? "vdr-you" : (isOnline ? "vdr-online" : null);

            row.Add(WrappedCell(label, "vdr-cell", nameState));
            row.Add(RowCell(player.MobKills.ToString("N0", Inv), "vdr-cell", true, ColMobsWidth, false, null));
            row.Add(RowCell(BossesKilledBy(player.Name).ToString("N0", Inv), "vdr-cell", true, ColBossesWidth, false, null));
            row.Add(RowCell(player.Deaths.ToString("N0", Inv), "vdr-cell", true, ColDeathsWidth, false, player.Deaths > 0 ? "vdr-death" : null));
            row.Add(RowCell(Math.Round(player.Damage).ToString("N0", Inv), "vdr-cell", true, ColDamageWidth, false, null));
            return row;
        }

        /// <summary>
        /// One row per boss kill of the week, newest first (the same boss killed twice is two rows):
        /// Boss | Killing blow | Highest hit received | Kills | When.
        ///
        /// Kills is the number of players THAT boss killed before it fell (the same player dying to it again counts again).
        /// Highest hit received is the biggest single hit it took, with the player who landed it and the weapon. Both come from
        /// the server; a server of an older build does not send them and the two columns then show "-".
        /// </summary>
        private bool BuildBossesTab()
        {
            _tableHeader.Add(RowCell("Boss", "vdr-head-cell", false, 0f, false, null));
            _tableHeader.Add(RowCell("Killing blow", "vdr-head-cell", false, ColKillerWidth, false, null));
            _tableHeader.Add(RowCell("Highest hit received", "vdr-head-cell", false, ColBossHitWidth, false, null));
            _tableHeader.Add(RowCell("Kills", "vdr-head-cell", true, ColBossKillsWidth, false, null));
            _tableHeader.Add(RowCell("When", "vdr-head-cell", false, ColWhenWidth, false, null));

            int count = _bossRows.Count;
            if (count == 0)
                return false;

            bool details = _data != null && _data.HasBossDetails;

            for (int i = 0; i < count; i++)
            {
                ReportBossKill kill = _bossRows[i];

                // As tall as its tallest cell: a long name or a long weapon wraps onto a second line.
                VisualElement row = MakeFlexRow((i & 1) == 0 ? "vdr-row-alt" : null);

                string killer = string.IsNullOrEmpty(kill.Killer) ? "-" : kill.Killer;
                bool killerIsYou = IsYou(kill.Killer);

                string hit = details ? FormatBossHit(kill) : "-";
                string kills = details ? kill.PlayerKills.ToString("N0", Inv) : "-";

                row.Add(WrappedCell(kill.Boss, "vdr-cell", null));
                row.Add(WrappedFixedCell(killer, "vdr-cell", false, ColKillerWidth, killerIsYou ? "vdr-you" : null));
                row.Add(WrappedFixedCell(hit, "vdr-cell", false, ColBossHitWidth, null));
                row.Add(WrappedFixedCell(kills, "vdr-cell", true, ColBossKillsWidth, details && kill.PlayerKills > 0 ? "vdr-death" : null));
                row.Add(WrappedFixedCell(kill.TimeUtc.ToLocalTime().ToString("ddd d MMM HH:mm", Inv), "vdr-cell", false, ColWhenWidth, null));
                _scroll.Content.Add(row);
            }

            return true;
        }

        /// <summary>"4,210 - Astrid (Iron sword)": the damage, then who landed the hit and with what. "-" when it is not known.</summary>
        private static string FormatBossHit(ReportBossKill kill)
        {
            if (kill.HighestHit <= 0.0)
                return "-";

            string text = Math.Round(kill.HighestHit).ToString("N0", Inv);
            bool hasPlayer = !string.IsNullOrEmpty(kill.HighestHitBy);
            bool hasWeapon = !string.IsNullOrEmpty(kill.HighestHitWeapon);

            if (hasPlayer && hasWeapon)
                return text + " \u2014 " + kill.HighestHitBy + " (" + kill.HighestHitWeapon + ")";

            if (hasPlayer)
                return text + " \u2014 " + kill.HighestHitBy;

            if (hasWeapon)
                return text + " (" + kill.HighestHitWeapon + ")";

            return text;
        }

        /// <summary>
        /// The Highest hits tab: Player | Damage | Weapon | Enemy, one row per player, biggest hit first. Every text cell wraps onto
        /// more lines instead of being cut with "...", and the row grows with its tallest cell. The first three columns have a
        /// percentage width (the header uses the same ones, so the columns line up), the Enemy column takes the rest of the row.
        /// </summary>
        private bool BuildHighestHits()
        {
            const float playerPct = 26f;
            const float damagePct = 12f;
            const float weaponPct = 30f;

            _tableHeader.Add(RowCell("Player", "vdr-head-cell", false, playerPct, true, null));
            _tableHeader.Add(RowCell("Damage", "vdr-head-cell", true, damagePct, true, null));
            _tableHeader.Add(RowCell("Weapon", "vdr-head-cell", false, weaponPct, true, null));
            _tableHeader.Add(RowCell("Enemy", "vdr-head-cell", false, 0f, false, null));

            int count = _hitRows.Count;
            if (count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                ReportPlayer player = _hitRows[i];
                bool isYou = IsYou(player.Name);

                // As tall as its tallest cell: a long player, weapon or enemy name wraps onto a second line.
                VisualElement row = MakeFlexRow(isYou ? "vdr-row-you" : ((i & 1) == 0 ? "vdr-row-alt" : null));

                string weapon = string.IsNullOrEmpty(player.HighestHitWeapon) ? "Unknown weapon" : player.HighestHitWeapon;
                string enemy = string.IsNullOrEmpty(player.HighestHitEnemy) ? "Unknown enemy" : player.HighestHitEnemy;

                row.Add(WrappedPercentCell(player.Name, "vdr-cell", false, playerPct, isYou ? "vdr-you" : null));
                row.Add(WrappedPercentCell(Math.Round(player.HighestHitDamage).ToString("N0", Inv), "vdr-cell", true, damagePct, null));
                row.Add(WrappedPercentCell(weapon, "vdr-cell", false, weaponPct, null));
                row.Add(WrappedCell(enemy, "vdr-cell", null));
                _scroll.Content.Add(row);
            }

            return true;
        }

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

        // ---------------------------------------------------------------- button

        /// <summary>
        /// A UI Toolkit Button. Its colours (normal, :hover, :active, :disabled) come from the style sheet; only the behaviour
        /// is set here: it is clicked with the mouse only (no keyboard focus), on one line.
        /// </summary>
        private sealed class Btn
        {
            public readonly Button Element;

            private bool _enabled = true;

            public Btn(Action onClick, params string[] classes)
            {
                Element = new Button();
                ResetBox(Element);

                foreach (string cssClass in classes)
                    Element.AddToClassList(cssClass);

                IStyle s = Element.style;
                s.whiteSpace = WhiteSpace.NoWrap;
                s.overflow = Overflow.Hidden;
                s.textOverflow = TextOverflow.Clip;

                // No keyboard focus: Space or Enter must not "click" a button the mouse used last.
                Element.focusable = false;
                Element.clicked += onClick;
            }

            /// <summary>Enables or disables the button. True when that changed it (the caller then restyles the button for :disabled).</summary>
            public bool SetEnabled(bool enabled)
            {
                if (_enabled == enabled)
                    return false;

                _enabled = enabled;
                Element.SetEnabled(enabled);
                return true;
            }
        }

        // ---------------------------------------------------------------- scrolling list

        /// <summary>
        /// A vertical scrolling area with a scrollbar, built from plain elements.
        ///
        /// UI Toolkit's own ScrollView is not used on purpose: its layout (and its scrollbar, a slider with a dragger) is
        /// defined by the runtime theme style sheet, which this window does not have. This does the little that is needed:
        /// the content sits in a clipping viewport and is moved with its top offset; the mouse wheel moves it three rows at a
        /// time; the thumb can be dragged and a click on the track pages up or down. Track and thumb are styled with the
        /// classes vdr-scroll-track and vdr-scroll-thumb.
        /// </summary>
        private sealed class ScrollPane : VisualElement
        {
            private const float WheelStep = 3f * RowHeight;
            private const float MinThumb = 24f;

            /// <summary>Add the rows here.</summary>
            public readonly VisualElement Content;

            private readonly VisualElement _viewport;
            private readonly VisualElement _track;
            private readonly VisualElement _thumb;

            private float _offset;
            private bool _thumbDown;
            private float _downPointerY;
            private float _downOffset;

            public ScrollPane()
            {
                ResetBox(this);
                style.flexDirection = FlexDirection.Row;

                _viewport = new VisualElement();
                ResetBox(_viewport);
                _viewport.style.flexGrow = 1f;
                _viewport.style.flexShrink = 1f;
                _viewport.style.minWidth = 0f;
                _viewport.style.overflow = Overflow.Hidden;

                // Absolutely positioned and without a height: it is as tall as its rows, whatever the viewport's height is.
                Content = new VisualElement();
                ResetBox(Content);
                Content.style.position = Position.Absolute;
                Content.style.left = 0f;
                Content.style.right = 0f;
                Content.style.top = 0f;
                Content.style.flexDirection = FlexDirection.Column;
                _viewport.Add(Content);

                _track = new VisualElement();
                _track.AddToClassList("vdr-scroll-track");
                ResetBox(_track);
                _track.style.width = ScrollbarWidth;
                _track.style.flexShrink = 0f;

                _thumb = new VisualElement();
                _thumb.AddToClassList("vdr-scroll-thumb");
                ResetBox(_thumb);
                _thumb.style.position = Position.Absolute;
                _thumb.style.left = 2f;
                _thumb.style.right = 2f;
                _thumb.style.top = 0f;
                _thumb.style.height = MinThumb;
                _thumb.style.display = DisplayStyle.None;
                _track.Add(_thumb);

                Add(_viewport);
                Add(_track);

                RegisterCallback<WheelEvent>(OnWheel);
                _viewport.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });
                Content.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });
                _track.RegisterCallback<GeometryChangedEvent>(delegate { Refresh(); });

                _track.RegisterCallback<PointerDownEvent>(OnTrackPointerDown);
                _thumb.RegisterCallback<PointerDownEvent>(OnThumbPointerDown);
                _thumb.RegisterCallback<PointerMoveEvent>(OnThumbPointerMove);
                _thumb.RegisterCallback<PointerUpEvent>(OnThumbPointerUp);
                _thumb.RegisterCallback<PointerCaptureOutEvent>(delegate { _thumbDown = false; });
            }

            /// <summary>How far the content is scrolled (pixels from the top). Kept within what the content allows.</summary>
            public float Offset
            {
                get { return _offset; }
                set
                {
                    _offset = Mathf.Max(0f, value);
                    Refresh();
                }
            }

            private void OnWheel(WheelEvent evt)
            {
                // Only the direction counts: how big a "step" is differs between mice, touchpads and input back ends.
                float direction = Mathf.Clamp(evt.delta.y, -1f, 1f);
                if (Mathf.Abs(direction) < 0.001f)
                    return;

                Offset = _offset + direction * WheelStep;
                evt.StopPropagation();
            }

            private void OnTrackPointerDown(PointerDownEvent evt)
            {
                // Clicks on the thumb are the thumb's; this is the empty track above or below it.
                if (evt.button != 0 || evt.target != _track)
                    return;

                float page = Mathf.Max(RowHeight, _viewport.layout.height * 0.9f);
                float clickY = ((Vector3)evt.localPosition).y;
                Offset = _offset + (clickY < _thumb.layout.y ? -page : page);
                evt.StopPropagation();
            }

            private void OnThumbPointerDown(PointerDownEvent evt)
            {
                if (evt.button != 0)
                    return;

                _thumbDown = true;
                _downPointerY = ((Vector3)evt.position).y;
                _downOffset = _offset;
                _thumb.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }

            private void OnThumbPointerMove(PointerMoveEvent evt)
            {
                if (!_thumbDown || !_thumb.HasPointerCapture(evt.pointerId))
                    return;

                float travel = _track.layout.height - _thumb.layout.height;
                float max = Content.layout.height - _viewport.layout.height;
                if (float.IsNaN(travel) || float.IsNaN(max) || travel <= 0.5f || max <= 0.5f)
                    return;

                float dy = ((Vector3)evt.position).y - _downPointerY;
                Offset = _downOffset + dy * max / travel;
                evt.StopPropagation();
            }

            private void OnThumbPointerUp(PointerUpEvent evt)
            {
                if (_thumb.HasPointerCapture(evt.pointerId))
                    _thumb.ReleasePointer(evt.pointerId);

                _thumbDown = false;
            }

            /// <summary>
            /// Clamps the offset to the content that is really there, moves the content and positions the thumb. Runs when the
            /// offset changes and whenever the viewport, the content or the track changes size.
            /// </summary>
            private void Refresh()
            {
                float viewHeight = _viewport.layout.height;
                float contentHeight = Content.layout.height;
                bool known = !float.IsNaN(viewHeight) && !float.IsNaN(contentHeight) && viewHeight > 0f;

                float max = known ? Mathf.Max(0f, contentHeight - viewHeight) : float.MaxValue;
                if (_offset > max)
                    _offset = max;

                Content.style.top = -_offset;

                if (!known || max <= 0.5f)
                {
                    _thumb.style.display = DisplayStyle.None;
                    return;
                }

                float trackHeight = _track.layout.height;
                if (float.IsNaN(trackHeight) || trackHeight <= 0f)
                    trackHeight = viewHeight;

                float thumbHeight = Mathf.Clamp(trackHeight * viewHeight / contentHeight, MinThumb, trackHeight);

                _thumb.style.display = DisplayStyle.Flex;
                _thumb.style.height = thumbHeight;
                _thumb.style.top = (trackHeight - thumbHeight) * (_offset / max);
            }
        }
    }
}
