using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Report;

internal static partial class ReportStats
{
    // ---------------------------------------------------------------- persistence (plain tab-separated text)
    //
    //   line 1:   header
    //   start     <UTC time, round-trip format>
    //   reported  <UTC time, same as start>   (present only when Discord accepted the report of that period; builds before this line existed ignore it)
    //   player    <name>  <1 if joined, else 0>  <mob kills>  <deaths>  <damage>
    //   hit       <n>  <highest hit damage>  <weapon>  <enemy>
    //   mobkill   <n>  <mob>  <count>
    //   mobdeath  <n>  <mob>  <count>
    //   deathcause <n>  <cause>  <count>
    //   mobhit    <n>  <mob>  <highest hit on that mob>   (builds before this line existed ignore it)
    //   deathhit  <n>  <cause>  <highest finishing hit>  <its damage type, may be empty>   (builds before this line existed ignore it)
    //   boss      <boss name>  <killer>  <UTC time>  <instance key>  <players it killed>  <highest hit>  <hit by>  <hit weapon>
    //             (the last five only in files of this build and later; older builds read the first four and ignore the rest)
    //   bossfight <instance key>  <players it killed>  <highest hit>  <hit by>  <hit weapon>  <UTC time of the last news>
    //             (a boss that was fought but not killed yet; builds before this line existed ignore it)
    //   bosskey   <boss object key>  <UTC time it was counted>   (oldest first; builds before this line existed ignore it)
    //
    // Names are URL-escaped, so they can never contain a tab or a line break.

    /// <summary>Caller must hold Sync. Returns false if there is no usable file.</summary>
    private static bool Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                return false;

            string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), FileHeader, StringComparison.Ordinal))
            {
                RelayDiagnostics.Warning("[Report] '" + _path + "' is not a weekly report data file; starting a new period.");
                return false;
            }

            bool haveStart = false;
            DateTime start = DateTime.UtcNow;
            DateTime reported = DateTime.MinValue;

            Players.Clear();
            BossKills.Clear();
            SeenBossKeys.Clear();
            BossFights.Clear();

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length == 0)
                    continue;

                if (parts[0] == "start" && parts.Length >= 2)
                {
                    haveStart = TryParseUtc(parts[1], out start);
                }
                else if (parts[0] == "reported" && parts.Length >= 2)
                {
                    if (!TryParseUtc(parts[1], out reported))
                        reported = DateTime.MinValue;
                }
                else if (parts[0] == "player" && parts.Length >= 6)
                {
                    string name = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    if (name == null)
                        continue;

                    ReportPlayer player = GetOrAddLocked(name);
                    if (player == null)
                        continue;

                    int kills;
                    int deaths;
                    double damage;
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out kills);
                    int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out deaths);
                    double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out damage);

                    player.Joined = parts[2] == "1";
                    player.MobKills = Math.Max(0, kills);
                    player.Deaths = Math.Max(0, deaths);
                    player.Damage = (double.IsNaN(damage) || double.IsInfinity(damage) || damage < 0.0) ? 0.0 : damage;
                }
                else if (parts[0] == "hit" && parts.Length >= 5)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    double hitDamage; double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage);
                    if (playerName != null && hitDamage > 0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        if (player != null)
                        {
                            player.HighestHitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                            player.HighestHitWeapon = Uri.UnescapeDataString(parts[3]);
                            // A file from an earlier build may hold a prefab name or key: shown in English from now on.
                            player.HighestHitEnemy = CreatureLabel(Uri.UnescapeDataString(parts[4]), null, "Unknown enemy");
                        }
                    }
                }
                else if (parts[0] == "mobkill" && parts.Length >= 4)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    string mobName = Uri.UnescapeDataString(parts[2]);
                    int count;
                    if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        if (player != null)
                        {
                            // Files from an earlier build hold prefab names ("Greydwarf_Elite"): converted to English here.
                            // Two old names can become the same English name, so the counts are added, not replaced.
                            string label = CreatureLabel(mobName, null, null);
                            if (label != null)
                                AddCount(player.MobKillsByType, label, count);
                        }
                    }
                }
                else if (parts[0] == "mobdeath" && parts.Length >= 4)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    string mobName = Uri.UnescapeDataString(parts[2]);
                    int count;
                    if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        if (player != null)
                        {
                            string label = CreatureLabel(mobName, null, null);
                            if (label != null)
                                AddCount(player.DeathsByMob, label, count);
                        }
                    }
                }
                else if (parts[0] == "mobhit" && parts.Length >= 4)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    string mobName = Uri.UnescapeDataString(parts[2]);
                    double hitDamage;
                    if (playerName != null &&
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage) &&
                        hitDamage > 0.0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        string label = CreatureLabel(mobName, null, null);
                        if (player != null && label != null)
                        {
                            // Two saved names can become the same English name: the higher hit wins.
                            double known;
                            hitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                            if (!player.MobBestHits.TryGetValue(label, out known) || hitDamage > known)
                                player.MobBestHits[label] = hitDamage;
                        }
                    }
                }
                else if (parts[0] == "deathhit" && parts.Length >= 5)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    string cause = Uri.UnescapeDataString(parts[2]);
                    string type = NormalizeName(Uri.UnescapeDataString(parts[4])) ?? string.Empty;
                    double hitDamage;
                    if (playerName != null &&
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hitDamage) &&
                        hitDamage > 0.0 && !double.IsNaN(hitDamage) && !double.IsInfinity(hitDamage))
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        string label = CreatureLabel(cause, null, null);
                        if (player != null && label != null)
                        {
                            // Two saved names can become the same English name: the higher hit wins.
                            hitDamage = Math.Min(hitDamage, MaxDamagePerEntry);
                            if (type.Length > MaxDamageTypeChars) type = type.Substring(0, MaxDamageTypeChars);
                            ReportDeathHit known;
                            if (!player.DeathCauseHits.TryGetValue(label, out known))
                                player.DeathCauseHits[label] = new ReportDeathHit { Damage = hitDamage, Type = type };
                            else if (hitDamage > known.Damage)
                            {
                                known.Damage = hitDamage;
                                known.Type = type;
                            }
                        }
                    }
                }
                else if (parts[0] == "deathcause" && parts.Length >= 4)
                {
                    string playerName = NormalizeName(Uri.UnescapeDataString(parts[1]));
                    string cause = Uri.UnescapeDataString(parts[2]); int count;
                    if (playerName != null && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                    {
                        ReportPlayer player = GetOrAddLocked(playerName);
                        if (player != null)
                        {
                            string label = CreatureLabel(cause, null, null);
                            if (label != null)
                                AddCount(player.DeathCauses, label, count);
                        }
                    }
                }
                else if (parts[0] == "boss" && parts.Length >= 4 && BossKills.Count < MaxBossKills)
                {
                    DateTime when;
                    if (!TryParseUtc(parts[3], out when))
                        when = DateTime.UtcNow;

                    ReportBossKill kill = new ReportBossKill
                    {
                        Boss = Uri.UnescapeDataString(parts[1]),
                        Killer = Uri.UnescapeDataString(parts[2]),
                        TimeUtc = when
                    };

                    // Fields 4-8 exist in files written by this build and later; a file from an earlier build has only the first
                    // four and loads with the per-boss numbers unknown (0 / empty).
                    if (parts.Length >= 9)
                    {
                        kill.InstanceKey = Uri.UnescapeDataString(parts[4]);

                        int playerKills;
                        double hit;
                        int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerKills);
                        double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out hit);

                        kill.PlayerKills = Math.Max(0, Math.Min(playerKills, MaxMobKillsPerEntry));
                        kill.HighestHit = (double.IsNaN(hit) || double.IsInfinity(hit) || hit < 0.0) ? 0.0 : Math.Min(hit, MaxDamagePerEntry);
                        kill.HighestHitBy = Uri.UnescapeDataString(parts[7]);
                        kill.HighestHitWeapon = Uri.UnescapeDataString(parts[8]);
                    }

                    BossKills.Add(kill);
                }
                else if (parts[0] == "bossfight" && parts.Length >= 7 && BossFights.Count < MaxBossFights)
                {
                    string instanceKey = Uri.UnescapeDataString(parts[1]);
                    if (instanceKey.Length > 0 && instanceKey.Length <= ReportProtocol.MaxNameChars)
                    {
                        int playerKills;
                        double hit;
                        DateTime updated;
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerKills);
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hit);
                        if (!TryParseUtc(parts[6], out updated))
                            updated = DateTime.UtcNow;

                        BossFights[instanceKey] = new BossFight
                        {
                            PlayerKills = Math.Max(0, Math.Min(playerKills, MaxMobKillsPerEntry)),
                            HighestHit = (double.IsNaN(hit) || double.IsInfinity(hit) || hit < 0.0) ? 0.0 : Math.Min(hit, MaxDamagePerEntry),
                            HighestHitBy = Uri.UnescapeDataString(parts[4]),
                            HighestHitWeapon = Uri.UnescapeDataString(parts[5]),
                            LastUpdateUtc = updated
                        };
                    }
                }
                else if (parts[0] == "bosskey" && parts.Length >= 3)
                {
                    // Entries too old, unreadable or repeated are skipped by the helper.
                    SeenBossKeys.TryLoad(Uri.UnescapeDataString(parts[1]), parts[2]);
                }
            }

            if (!haveStart)
            {
                RelayDiagnostics.Warning("[Report] '" + _path + "' has no period start; starting a new period.");
                Players.Clear();
                BossKills.Clear();
                SeenBossKeys.Clear();
                BossFights.Clear();
                return false;
            }

            PruneBossFightsLocked();

            _periodStartUtc = start;
            _deliveredPeriodStartUtc = reported;
            return true;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Warning("[Report] Could not read '" + _path + "': " + ex.Message + " Starting a new period.");
            Players.Clear();
            BossKills.Clear();
            SeenBossKeys.Clear();
            BossFights.Clear();
            return false;
        }
    }

    /// <summary>Caller must hold Sync.</summary>
    private static void SaveLocked()
    {
        if (string.IsNullOrEmpty(_path))
            return;

        try
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append(FileHeader).Append('\n');
            sb.Append("start\t").Append(_periodStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            if (_deliveredPeriodStartUtc == _periodStartUtc)
                sb.Append("reported\t").Append(_deliveredPeriodStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
            {
                sb.Append("player\t")
                  .Append(Uri.EscapeDataString(player.Name)).Append('\t')
                  .Append(player.Joined ? '1' : '0').Append('\t')
                  .Append(player.MobKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(player.Deaths.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(player.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }

            foreach (ReportPlayer player in Players.Values)
                if (player.HighestHitDamage > 0)
                    sb.Append("hit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(player.HighestHitDamage.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Uri.EscapeDataString(player.HighestHitWeapon ?? string.Empty)).Append('\t')
                      .Append(Uri.EscapeDataString(player.HighestHitEnemy ?? string.Empty)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
                foreach (KeyValuePair<string, int> mob in player.MobKillsByType)
                    sb.Append("mobkill\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(Uri.EscapeDataString(mob.Key)).Append('\t').Append(mob.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
                foreach (KeyValuePair<string, int> mob in player.DeathsByMob)
                    sb.Append("mobdeath\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(Uri.EscapeDataString(mob.Key)).Append('\t').Append(mob.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
                foreach (KeyValuePair<string, double> hit in player.MobBestHits)
                    sb.Append("mobhit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(Uri.EscapeDataString(hit.Key)).Append('\t').Append(hit.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
                foreach (KeyValuePair<string, ReportDeathHit> hit in player.DeathCauseHits)
                    sb.Append("deathhit\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(Uri.EscapeDataString(hit.Key)).Append('\t')
                      .Append(hit.Value.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Uri.EscapeDataString(hit.Value.Type ?? string.Empty)).Append('\n');

            foreach (ReportPlayer player in Players.Values)
                foreach (KeyValuePair<string, int> cause in player.DeathCauses)
                    sb.Append("deathcause\t").Append(Uri.EscapeDataString(player.Name)).Append('\t')
                      .Append(Uri.EscapeDataString(cause.Key)).Append('\t').Append(cause.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (ReportBossKill kill in BossKills)
            {
                sb.Append("boss\t")
                  .Append(Uri.EscapeDataString(kill.Boss ?? string.Empty)).Append('\t')
                  .Append(Uri.EscapeDataString(kill.Killer ?? string.Empty)).Append('\t')
                  .Append(kill.TimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Uri.EscapeDataString(kill.InstanceKey ?? string.Empty)).Append('\t')
                  .Append(kill.PlayerKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(kill.HighestHit.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Uri.EscapeDataString(kill.HighestHitBy ?? string.Empty)).Append('\t')
                  .Append(Uri.EscapeDataString(kill.HighestHitWeapon ?? string.Empty)).Append('\n');
            }

            foreach (KeyValuePair<string, BossFight> fight in BossFights)
            {
                sb.Append("bossfight\t")
                  .Append(Uri.EscapeDataString(fight.Key)).Append('\t')
                  .Append(fight.Value.PlayerKills.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(fight.Value.HighestHit.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Uri.EscapeDataString(fight.Value.HighestHitBy ?? string.Empty)).Append('\t')
                  .Append(Uri.EscapeDataString(fight.Value.HighestHitWeapon ?? string.Empty)).Append('\t')
                  .Append(fight.Value.LastUpdateUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            }

            // Oldest first, so loading them in file order keeps the newest ones if there are more than the limit.
            foreach (KeyValuePair<string, DateTime> seen in SeenBossKeys.Entries)
            {
                sb.Append("bosskey\t")
                  .Append(Uri.EscapeDataString(seen.Key)).Append('\t')
                  .Append(RememberedKeys.FormatTime(seen.Value)).Append('\n');
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(temp, _path, true);
            File.Delete(temp);

            _dirty = false;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Warning("[Report] Could not save '" + _path + "': " + ex.Message);
        }
    }

    private static bool TryParseUtc(string text, out DateTime value)
    {
        return DateTime.TryParse(
            text == null ? null : text.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }
}
