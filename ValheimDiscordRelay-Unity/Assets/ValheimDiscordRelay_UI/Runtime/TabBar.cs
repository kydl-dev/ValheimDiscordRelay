using System;
using System.Collections.Generic;
using KydlCore.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// A row of tabs in the shared KydlCore.Ui look (flat buttons, the chosen one gold with a gold underline). Shared through the mod template (Claude\template, from WhipItUp, user 2026-10-27). Root goes into the window; `changed` gets the index of the tab that was picked.
    /// </summary>
    internal sealed class TabBar
    {
        const float RailMargin = 18f;      // the window body's own side padding, so the bar lines up with the content below it

        readonly List<Button> _buttons = new List<Button>();
        readonly Action<int> _changed;
        int _selected = -1;

        internal VisualElement Root { get; private set; }
        internal int Selected { get { return _selected; } }

        /// Builds the bar with `names` and selects `selected` (the first call does not fire `changed`).
        internal TabBar(IList<string> names, int selected, Action<int> changed)
        {
            _changed = changed;
            Root = new VisualElement { name = "kydl-tabs" };
            Root.style.flexDirection = FlexDirection.Row;
            Root.style.flexShrink = 0;
            Root.style.marginLeft = RailMargin; Root.style.marginRight = RailMargin; Root.style.marginTop = 8;
            Root.style.borderBottomWidth = 1; Root.style.borderBottomColor = UiKit.Border;
            for (int i = 0; i < names.Count; i++)
            {
                int index = i;
                var b = new Button(() => Select(index)) { text = names[i] };
                b.style.fontSize = UiKit.ButtonSize; b.style.flexShrink = 0;
                b.style.marginLeft = 0; b.style.marginRight = 4; b.style.marginTop = 0; b.style.marginBottom = 0;
                UiKit.Pad(b, 8, 22);
                b.style.borderTopWidth = 0; b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0; b.style.borderBottomWidth = 3;
                UiKit.Radius(b, 0);
                UiKit.Fade(b, "background-color", "color");
                b.RegisterCallback<PointerEnterEvent>(e => Paint(index, true));
                b.RegisterCallback<PointerLeaveEvent>(e => Paint(index, false));
                _buttons.Add(b);
                Root.Add(b);
            }
            Show(Mathf.Clamp(selected, 0, Math.Max(0, names.Count - 1)));
            UiLog.Verbose("TabBar: built with " + names.Count + " tab(s) [" + string.Join(", ", names) + "], selected " + _selected);
        }

        /// Picks a tab. The same tab again does nothing. Expected outcome: returns whether `index` is the selected tab afterwards.
        internal bool Select(int index)
        {
            if (index < 0 || index >= _buttons.Count)
            {
                UiLog.Verbose("TabBar: tab " + index + " does not exist (" + _buttons.Count + " tabs), selection stays " + _selected);
                return _selected == index;
            }
            if (index == _selected) return _selected == index;
            UiLog.Verbose("TabBar: tab " + _selected + " -> " + index + " ('" + _buttons[index].text + "')");
            Show(index);
            if (_changed != null) _changed(index);
            return _selected == index;
        }

        void Show(int index)
        {
            _selected = index;
            for (int i = 0; i < _buttons.Count; i++) Paint(i, false);
        }

        void Paint(int index, bool hover)
        {
            var b = _buttons[index];
            bool active = index == _selected;
            b.style.backgroundColor = active ? UiKit.PanelLight : hover ? UiKit.PanelHover : Color.clear;
            b.style.color = active ? UiKit.Gold : hover ? UiKit.Text : UiKit.TextDim;
            b.style.borderBottomColor = active ? UiKit.Gold : Color.clear;
        }
    }
}
