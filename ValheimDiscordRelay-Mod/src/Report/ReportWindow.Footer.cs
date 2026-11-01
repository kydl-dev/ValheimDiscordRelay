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
    }
}
