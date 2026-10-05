using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
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

        /// <summary>The attacker's prefab name without "(Clone)" (e.g. "Boar", "Bjorn", "Bat_swamp"), or null.</summary>
        internal string PrefabName { get; private set; }

        /// <summary>True if the prefab is one of the animals listed in <see cref="DeathQuotes"/>. Never true for players or bosses.</summary>
        internal bool IsAnimal { get; private set; }

        /// <summary>The ZDOID stored in HitData.m_attacker when this snapshot was taken.</summary>
        internal ZDOID AttackerId { get; private set; }

        /// <summary>Time.unscaledTime at capture.</summary>
        internal float CapturedAt { get; private set; }

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
}
