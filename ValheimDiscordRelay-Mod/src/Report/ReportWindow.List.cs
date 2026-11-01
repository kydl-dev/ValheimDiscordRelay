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
    }
}
