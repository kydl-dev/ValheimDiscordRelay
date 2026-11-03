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
        /// The key that identifies ONE boss for the weekly report's per-boss numbers (players it killed, highest hit it
        /// received): the boss object's own key, so a boss that is fought again after a wipe is still the same boss.
        ///
        /// A multi-phase boss (Kall Fimbulbringer) is a different object in every phase, so its object key cannot tie the
        /// phases together. All phases share one key per boss type instead ("fight:frozenking"); the numbers collected under
        /// it are handed to the boss kill when the final phase dies, and the key is free again after that.
        /// </summary>
        /// <param name="prefab">The boss's prefab name (may carry "(Clone)"); may be null when only the object key is known.</param>
        /// <param name="objectKey">The key of the boss object (BossAnnouncer.MakeKey).</param>
        internal static string InstanceKey(string prefab, string objectKey)
        {
            string family = PhaseFamilyOfPrefab(prefab);
            return family == null ? objectKey : FightKeyPrefix + family.ToLowerInvariant();
        }

        /// <summary>
        /// Same as <see cref="InstanceKey(string,string)"/> for the final kill, where the server knows the boss's display name
        /// ("Kall Fimbulbringer") and not the prefab.
        /// </summary>
        internal static string InstanceKeyForName(string bossDisplayName, string objectKey)
        {
            string name = StripTags(bossDisplayName);
            if (name.Length == 0)
                return objectKey;

            foreach (KeyValuePair<string, string> boss in MultiPhaseBosses)
            {
                BossInfo info;
                if (ByPrefab.TryGetValue(boss.Value, out info) &&
                    string.Equals(info.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return FightKeyPrefix + boss.Key.ToLowerInvariant();
            }

            return objectKey;
        }

        private const string FightKeyPrefix = "fight:";

        private static string PhaseFamilyOfPrefab(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab))
                return null;

            string name = CleanPrefabName(prefab);
            foreach (string prefix in MultiPhaseBosses.Keys)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            return null;
        }

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
        /// Besides the inline characters this covers what only counts at the start of a line: a heading ("# x", "## x",
        /// "### x"), a small-text line ("-# x"), a bullet ("- x") and a numbered list ("1. x"). A name is not always at the
        /// start of a line in the final message, but Discord removes a backslash before any punctuation character, so an
        /// escape that was not needed is never visible. (Digits cannot be escaped; for "1. x" the dot is.)
        /// </summary>
        internal static string EscapeDiscordMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder sb = new StringBuilder(value.Length + 8);
            bool atLineStart = true;   // only blanks seen since the start of the text or of the last line

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (c == '\n' || c == '\r')
                {
                    atLineStart = true;
                    sb.Append(c);
                    continue;
                }

                if (c == ' ' || c == '\t')
                {
                    sb.Append(c);
                    continue;
                }

                if (atLineStart)
                {
                    atLineStart = false;

                    if (c == '#' || c == '-')
                    {
                        sb.Append('\\').Append(c);
                        continue;
                    }

                    if (c >= '0' && c <= '9')
                    {
                        // "12. text": escape the dot so it is not a numbered list.
                        int j = i;
                        while (j < value.Length && value[j] >= '0' && value[j] <= '9')
                            j++;

                        if (j < value.Length && value[j] == '.')
                        {
                            sb.Append(value, i, j - i).Append("\\.");
                            i = j;
                            continue;
                        }
                    }
                }

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
