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

        if (!ReportConfig.On)
        {
            Notify("The weekly report is turned off on this server.");
            return;
        }

        if (!ReportConfig.WindowOn)
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
}
