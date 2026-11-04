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
            ConfigFile file = ModSettings.File;
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
}
