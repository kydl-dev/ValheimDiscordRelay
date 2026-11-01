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
    public sealed partial class ReportWindow
    {
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
    }
}
