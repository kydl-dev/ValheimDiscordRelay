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
    }
}
