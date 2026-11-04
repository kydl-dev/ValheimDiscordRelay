using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Immutable snapshot of the creature (or player) that damaged the local player.
/// Captured at hit time, because the attacker's Character object may already be
/// dead or despawned by the time the local player actually dies.
/// </summary>
internal sealed class AttackerInfo
{
    private static readonly Regex ColorOpenTag =
        new("<color=([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.CultureInvariant);

    /// <summary>Highest star count shown, so modded high levels can't flood a message.</summary>
    private const int MaxStars = 5;

    /// <summary>Name with all rich-text tags removed, whitespace-normalised, Discord-safe.</summary>
    internal string Name { get; private set; }

    /// <summary>True if Valheim's display name carried a &lt;color=...&gt; tag.</summary>
    internal bool HasColorTag { get; private set; }

    /// <summary>The value of the first colour tag (e.g. "orange"), or null.</summary>
    internal string ColorValue { get; private set; }

    /// <summary>Creature level as reported by Valheim: 1 = no stars, 2 = one star, 3 = two stars.</summary>
    internal int Level { get; private set; }

    /// <summary>True for real bosses and for any enemy whose display name carries a colour tag.</summary>
    internal bool IsBoss { get; private set; }

    internal bool IsPlayer { get; private set; }

    /// <summary>
    /// True only when the game itself says the creature is a boss (Character.IsBoss). Unlike <see cref="IsBoss"/> it is
    /// not set for an enemy that merely has a colour tag in its name; the weekly report counts only these as bosses.
    /// </summary>
    internal bool IsGameBoss { get; private set; }

    /// <summary>The attacker's prefab name without "(Clone)" (e.g. "Boar", "Bjorn", "Bat_swamp"), or null.</summary>
    internal string PrefabName { get; private set; }

    /// <summary>
    /// The creature's name as the game stores it (Character.m_name, a localization key such as "$enemy_greydwarf"), or
    /// null for a player or when it cannot be read. Unlike <see cref="Name"/> it does not depend on the game's language,
    /// so the weekly report uses it to name the killer in English (see EnglishNames).
    /// </summary>
    internal string NameKey { get; private set; }

    /// <summary>True if the prefab is one of the animals listed in <see cref="DeathQuotes"/>. Never true for players or bosses.</summary>
    internal bool IsAnimal { get; private set; }

    /// <summary>The ZDOID stored in HitData.m_attacker when this snapshot was taken.</summary>
    internal ZDOID AttackerId { get; private set; }

    /// <summary>
    /// The attacker's object key, the same text the boss announcement and the weekly report use to tell two bosses of the
    /// same kind apart (BossAnnouncer.MakeKey), or null when the hit carried no attacker.
    /// </summary>
    internal string ObjectKey
    {
        get
        {
            try
            {
                // No attacker in the hit: the ZDOID is the empty one (user 0, id 0).
                if (AttackerId.UserID == 0L && AttackerId.ID == 0u)
                    return null;

                return ValheimDiscordRelay.Server.BossAnnouncer.MakeKey(AttackerId.UserID, (long)AttackerId.ID);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Time.unscaledTime at capture.</summary>
    internal float CapturedAt { get; private set; }

    /// <summary>World position of the attacker's centre when this snapshot was taken (valid when <see cref="HasPosition"/>).</summary>
    internal Vector3 Position { get; private set; }

    internal bool HasPosition { get; private set; }

    /// <summary>Number of stars to display (0 for level 1).</summary>
    internal int StarCount
    {
        get { return Mathf.Clamp(Level - 1, 0, MaxStars); }
    }

    /// <summary>
    /// Builds a snapshot from a live Character.
    /// </summary>
    /// <param name="attacker">The attacking character.</param>
    /// <param name="attackerId">HitData.m_attacker of the hit that identified it.</param>
    /// <returns>The snapshot, or null if the character has no usable name.</returns>
    internal static AttackerInfo FromCharacter(Character attacker, ZDOID attackerId)
    {
        if (attacker == null)
            return null;

        string raw;
        try
        {
            raw = attacker.GetHoverName();
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read attacker name: " + ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string plain = StripTags(raw);
        if (string.IsNullOrWhiteSpace(plain))
            return null;

        AttackerInfo info = new AttackerInfo();
        info.Name = plain;
        info.AttackerId = attackerId;
        info.CapturedAt = Time.unscaledTime;
        info.IsPlayer = attacker is Player;

        try
        {
            info.Position = attacker.GetCenterPoint();
            info.HasPosition = true;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read attacker position: " + ex.Message);
        }

        Match color = ColorOpenTag.Match(raw);
        if (color.Success)
        {
            info.HasColorTag = true;
            info.ColorValue = color.Groups[1].Value.Trim();
        }

        try
        {
            info.Level = Mathf.Max(1, attacker.GetLevel());
        }
        catch
        {
            info.Level = 1;
        }

        try
        {
            info.IsBoss = attacker.IsBoss();
        }
        catch
        {
            info.IsBoss = false;
        }

        info.IsGameBoss = info.IsBoss && !info.IsPlayer;

        // Enemies whose display name carries a colour tag are special/boss-like: treat them as bosses.
        if (info.HasColorTag)
            info.IsBoss = true;

        try
        {
            if (attacker.gameObject != null)
                info.PrefabName = BossCatalog.CleanPrefabName(attacker.gameObject.name);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read attacker prefab name: " + ex.Message);
        }

        if (!info.IsPlayer)
        {
            try
            {
                info.NameKey = attacker.m_name;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Debug("Could not read attacker name key: " + ex.Message);
            }
        }

        info.IsAnimal = !info.IsPlayer && !info.IsBoss && DeathQuotes.IsAnimalPrefab(info.PrefabName);

        return info;
    }

    /// <summary>Repeats <paramref name="glyph"/> once per star.</summary>
    internal string BuildStars(string glyph)
    {
        int count = StarCount;
        if (count <= 0)
            return string.Empty;

        StringBuilder sb = new StringBuilder(count * glyph.Length);
        for (int i = 0; i < count; i++)
            sb.Append(glyph);
        return sb.ToString();
    }

    /// <summary>
    /// Removes every rich-text tag, collapses line breaks, neutralises Discord code fences and
    /// caps the length.
    /// </summary>
    private static string StripTags(string value)
    {
        string s = AnyTag.Replace(value, string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("`", "'")
            .Trim();

        if (s.Length > 120)
            s = s.Substring(0, 119) + "…";

        return s;
    }
}
