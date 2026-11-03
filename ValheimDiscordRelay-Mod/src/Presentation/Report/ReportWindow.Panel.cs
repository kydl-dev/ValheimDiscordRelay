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
            string configFile = ModSettings.File != null ? ModSettings.File.ConfigFilePath : null;
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
    }
}
