using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// Static presentation data for one boss.
    /// </summary>
    internal sealed class BossInfo
    {
        internal readonly string Prefab;
        internal readonly string DisplayName;

        /// <summary>Used both as the webhook avatar and as the embed thumbnail.</summary>
        internal readonly string AvatarUrl;

        /// <summary>Large image shown below the embed description.</summary>
        internal readonly string PhotoUrl;

        internal BossInfo(string prefab, string displayName, string avatarUrl, string photoUrl)
        {
            Prefab = prefab;
            DisplayName = displayName;
            AvatarUrl = avatarUrl;
            PhotoUrl = photoUrl;
        }
    }

    /// <summary>
    /// Lookup of the known bosses by Valheim prefab name (locale-independent), and text helpers
    /// shared by the client and the server for boss announcements.
    /// </summary>
    internal static class BossCatalog
    {
        private static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, BossInfo> ByPrefab = new(StringComparer.OrdinalIgnoreCase);

        static BossCatalog()
        {
            Add("Eikthyr", "Eikthyr",
                "https://valheim.weirdgloop.org/images/Boss1Eikthyr_unlocked.jpg?44822",
                "https://www.valheim.tools/renders/creatures/Eikthyr.png");

            Add("gd_king", "The Elder",
                "https://valheim.weirdgloop.org/images/Boss2Elder_unlocked.jpg?9180f",
                "https://www.valheim.tools/renders/creatures/gd_king.png");

            Add("Bonemass", "Bonemass",
                "https://valheim.weirdgloop.org/images/Boss3Bonemass_unlocked.jpg?44822",
                "https://www.valheim.tools/renders/creatures/Bonemass.png");

            Add("Dragon", "Moder",
                "https://valheim.weirdgloop.org/images/Boss4Moder_unlocked.jpg?44822",
                "https://www.valheim.tools/renders/creatures/Dragon.png");

            Add("GoblinKing", "Yagluth",
                "https://valheim.weirdgloop.org/images/Boss5Yagluth_unlocked.jpg?9180f",
                "https://www.valheim.tools/renders/creatures/GoblinKing.png");

            Add("SeekerQueen", "The Queen",
                "https://valheim.weirdgloop.org/images/Boss6Queen_unlocked.jpg?9180f",
                "https://www.valheim.tools/renders/creatures/SeekerQueen.png");

            Add("Fader", "Fader",
                "https://valheim.weirdgloop.org/images/Boss7Fader_unlocked.jpg?9180f",
                "https://www.valheim.tools/renders/creatures/Fader.png");

            // Kall fights in three phases; only the death of the final phase (FrozenKing_p3) is announced
            // (see MultiPhaseBosses below).
            Add("FrozenKing_p3", "Kall Fimbulbringer",
                "https://valheim.weirdgloop.org/images/Boss8FrozenKing_unlocked.jpg?9180f",
                "https://www.valheim.tools/renders/creatures/FrozenKing.png");
        }

        private static void Add(string prefab, string displayName, string avatarUrl, string photoUrl)
        {
            ByPrefab[prefab] = new BossInfo(prefab, displayName, avatarUrl, photoUrl);
        }

        /// <summary>
        /// Discord embeds have no image alignment: a large image is drawn left-aligned, and only fills the width
        /// (so looks centred) when its own canvas is wide. This way the picture is served through the wsrv.nl image proxy,
        /// which letterboxes it onto a transparent 4:3 canvas with
        /// the boss photo in the middle. Set to false to use the original image URL directly.
        /// </summary>
        private static readonly bool CenterPhoto = true;

        private const string CenterPhotoOptions = "&w=800&h=600&fit=contain&cbg=00000000&output=png";

        /// <summary>
        /// Returns the URL to put in the embed's large image so the boss appears centred.
        /// </summary>
        /// <param name="photoUrl">The boss's original render URL.</param>
        internal static string CenteredPhotoUrl(string photoUrl)
        {
            if (!CenterPhoto || string.IsNullOrEmpty(photoUrl))
                return photoUrl;

            return "https://wsrv.nl/?url=" + Uri.EscapeDataString(photoUrl) + CenterPhotoOptions;
        }

        /// <summary>
        /// Bosses that fight in several phases, each phase being its own prefab and its own object that dies
        /// when the phase ends. Only the death of the final phase is a real boss kill. Key: prefab-name prefix shared
        /// by all phases; value: the prefab of the final phase.
        /// </summary>
        private static readonly Dictionary<string, string> MultiPhaseBosses = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FrozenKing", "FrozenKing_p3" }
        };

        /// <summary>
        /// True if the prefab is a phase of a multi-phase boss that is NOT the last one (e.g. FrozenKing_p1 / _p2),
        /// so its death must not be announced.
        /// </summary>
        internal static bool IsIntermediatePhase(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab))
                return false;

            string name = CleanPrefabName(prefab);
            foreach (KeyValuePair<string, string> boss in MultiPhaseBosses)
            {
                if (name.StartsWith(boss.Key, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, boss.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True if both prefabs are phases of the same multi-phase boss (used to merge the damage of all phases).
        /// </summary>
        internal static bool IsSamePhaseFamily(string prefabA, string prefabB)
        {
            if (string.IsNullOrWhiteSpace(prefabA) || string.IsNullOrWhiteSpace(prefabB))
                return false;

            string a = CleanPrefabName(prefabA);
            string b = CleanPrefabName(prefabB);
            foreach (string prefix in MultiPhaseBosses.Keys)
            {
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static bool TryGet(string prefab, out BossInfo info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(prefab))
                return false;

            return ByPrefab.TryGetValue(CleanPrefabName(prefab), out info);
        }

        /// <summary>
        /// Removes Unity's "(Clone)" suffix from an instantiated object's name.
        /// </summary>
        internal static string CleanPrefabName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            int index = name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                name = name.Substring(0, index);

            return name.Trim();
        }

        /// <summary>
        /// Strips rich-text tags and line breaks from a name coming from the game.
        /// </summary>
        internal static string StripTags(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return AnyTag.Replace(value, string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        /// <summary>
        /// Upper-cases the first character and leaves the rest untouched ("lone wolf" -> "Lone wolf",
        /// "The Elder" -> "The Elder").
        /// </summary>
        internal static string CapitalizeFirst(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        /// <summary>
        /// Backslash-escapes Discord markdown characters so a player name cannot restyle the message.
        /// </summary>
        internal static string EscapeDiscordMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                    case '*':
                    case '_':
                    case '~':
                    case '`':
                    case '|':
                    case '>':
                    case '[':
                    case ']':
                        sb.Append('\\');
                        break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
