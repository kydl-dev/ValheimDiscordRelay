using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay
{
    /// <summary>
    /// English display names for the things the weekly report shows (the weapon of a player's highest hit).
    ///
    /// Valheim's item names are localization keys such as "$item_sword_iron", which the game turns into text in the language
    /// the player chose. The weekly report is read by the whole server and posted to Discord, so it must not depend on the
    /// language of whoever landed the hit, and it must never show a key or a prefab name such as "SwordIron".
    ///
    /// How a name becomes English, in order:
    ///   1. The game's own English text. Only used while the game that resolves the name is itself set to English: asking the
    ///      game for text in another language would not give English, and switching the live game's language to find out
    ///      would disturb the running UI.
    ///   2. Otherwise the key is passed on unresolved (see <see cref="FromGame"/>), and the server, which is normally
    ///      English, resolves it with step 1 when it records the hit (see <see cref="ToEnglish"/>).
    ///   3. If no game is in English, a plain English name is built from the key itself: "$item_sword_iron" becomes
    ///      "Iron sword". Names of weapons that were stored by an earlier version as prefab names ("SwordIron") get the
    ///      same treatment.
    ///
    /// Creatures (mobs, the thing that killed a player, the enemy of a highest hit) follow the same idea, see
    /// <see cref="CreatureFromGame"/> and <see cref="CreatureToEnglish"/>, with one extra step: a small built-in table of the
    /// game's own creature names, keyed by prefab name ("Greydwarf_Elite" -> "Greydwarf Brute"), used when no game in the
    /// chain can give English text. The table is a fallback; the game's own English text always wins.
    /// </summary>
    internal static class EnglishNames
    {
        private const string English = "English";

        /// <summary>Words that end a key ("sword_iron") but start the English name ("Iron sword").</summary>
        private static readonly string[] MaterialWords =
        {
            "wood", "flint", "stone", "bone", "antler", "bronze", "copper", "iron", "silver",
            "blackmetal", "chitin", "crystal", "obsidian", "elderbark", "ancientbark", "flametal"
        };

        /// <summary>Splits "SwordIron" and "THSwordKrom" into words.</summary>
        private static readonly Regex CamelBoundary = new(
            "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant);

        private static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.CultureInvariant);

        // Remembers what the game resolved, so the per-hit weapon lookup stays cheap. Main thread only. Dropped when the
        // game's language changes.
        private static readonly Dictionary<string, string> GameCache = new(StringComparer.Ordinal);
        private static string _cacheLanguage;

        // ---------------------------------------------------------------- entry points

        /// <summary>
        /// For the game that measures a hit: the best name it can give right now.
        /// Returns English text when this game is set to English, otherwise the unresolved key (for the server to finish),
        /// or, for a name that is already plain text, that text.
        /// </summary>
        /// <param name="sharedName">The item's m_shared.m_name (a key like "$item_sword_iron", or plain text for some mods).</param>
        /// <param name="prefabName">The item's prefab name, used only when the item has no usable name.</param>
        internal static string FromGame(string sharedName, string prefabName)
        {
            string name = Clean(sharedName);

            if (name.Length == 0)
                return ToEnglish(prefabName);

            if (!IsKey(name))
                return name;

            string english = TryGameEnglish(name);
            return english ?? name;
        }

        /// <summary>
        /// For the game that records the statistics: whatever arrives or is stored is turned into an English name.
        /// A key is resolved by this game if it is in English, or built from the key's own words; a prefab name is turned
        /// into words; any other text is left as it is. Never returns null.
        /// </summary>
        /// <param name="name">A key, a prefab name or an item name.</param>
        internal static string ToEnglish(string name)
        {
            string text = Clean(name);
            if (text.Length == 0)
                return string.Empty;

            if (IsKey(text))
                return TryGameEnglish(text) ?? FromWords(text, true);

            if (LooksLikePrefabName(text))
                return FromWords(text, true);

            return text;
        }

        // ---------------------------------------------------------------- creatures

        /// <summary>
        /// Display names of the vanilla creatures, by prefab name (case-insensitive). Only used when the game cannot give
        /// English text itself, and only for names that are certain; any creature not listed here gets a name built from
        /// its key or prefab words instead. Bosses are not listed: <see cref="BossCatalog"/> knows them.
        /// </summary>
        private static readonly Dictionary<string, string> CreatureByPrefab = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Boar", "Boar" },
            { "Boar_piggy", "Piglet" },
            { "Neck", "Neck" },
            { "Greyling", "Greyling" },
            { "Deer", "Deer" },
            { "Greydwarf", "Greydwarf" },
            { "Greydwarf_Elite", "Greydwarf Brute" },
            { "Greydwarf_Shaman", "Greydwarf Shaman" },
            { "Troll", "Troll" },
            { "Skeleton", "Skeleton" },
            { "Skeleton_NoArcher", "Skeleton" },
            { "Skeleton_Poison", "Rancid Remains" },
            { "Ghost", "Ghost" },
            { "Surtling", "Surtling" },
            { "Draugr", "Draugr" },
            { "Draugr_Elite", "Draugr Elite" },
            { "Draugr_Ranged", "Draugr Archer" },
            { "Blob", "Blob" },
            { "BlobElite", "Oozer" },
            { "BlobTar", "Growth" },
            { "Leech", "Leech" },
            { "Wraith", "Wraith" },
            { "Abomination", "Abomination" },
            { "Serpent", "Serpent" },
            { "Wolf", "Wolf" },
            { "Hatchling", "Drake" },
            { "Fenring", "Fenring" },
            { "StoneGolem", "Stone Golem" },
            { "Bat", "Bat" },
            { "Goblin", "Fuling" },
            { "GoblinArcher", "Fuling Archer" },
            { "GoblinBrute", "Fuling Berserker" },
            { "GoblinShaman", "Fuling Shaman" },
            { "Lox", "Lox" },
            { "Deathsquito", "Deathsquito" },
            { "Hare", "Hare" },
            { "Volture", "Vulture" },
            { "Seeker", "Seeker" },
            { "SeekerBrute", "Seeker Soldier" },
            { "SeekerBrood", "Seeker Brood" },
            { "Tick", "Tick" },
            { "Gjall", "Gjall" },
            { "Dverger", "Dverger" },
            { "Charred_Melee", "Charred Warrior" },
            { "Charred_Archer", "Charred Archer" },
            { "Charred_Mage", "Charred Mage" },
            { "Asksvin", "Asksvin" },
            { "Morgen", "Morgen" },
            { "FallenValkyrie", "Fallen Valkyrie" }
        };

        /// <summary>Key prefixes that only say what kind of creature it is ("$enemy_greydwarf" -> "greydwarf").</summary>
        private static readonly string[] CreatureKeyPrefixes = { "enemy_", "animal_", "npc_", "boss_" };

        /// <summary>
        /// For the game that measures a hit or a death: the best name it can give right now for a creature.
        /// Returns, in order: the game's English text for the creature's name key (when this game is set to English);
        /// the built-in English name of the prefab; the unresolved key, for the server to finish (see
        /// <see cref="CreatureToEnglish"/>); and for a creature without a key, a name built from its prefab. Plain text
        /// (some mods name their creatures without a key) is returned as it is. Never returns null.
        /// </summary>
        /// <param name="nameKey">The creature's Character.m_name (a key like "$enemy_greydwarf", or plain text).</param>
        /// <param name="prefabName">The creature's prefab name; "(Clone)" is ignored.</param>
        internal static string CreatureFromGame(string nameKey, string prefabName)
        {
            string key = Clean(nameKey);
            string prefab = BossCatalog.CleanPrefabName(prefabName);

            if (key.Length == 0)
                return CreatureToEnglish(prefab, null);

            if (!IsKey(key))
                return key;

            string english = TryGameEnglish(key);
            if (english != null)
                return english;

            string known = FromCreatureTable(prefab);
            return known ?? key;
        }

        /// <summary>
        /// For the game that records the statistics: whatever arrives or is stored for a creature (a name key, a prefab name
        /// such as "Greydwarf_Elite", or a name that already is English) is turned into an English name. Never returns null.
        /// An English name (anything with a space, or a single plain word) is left as it is.
        /// </summary>
        /// <param name="name">A key, a prefab name or an English name.</param>
        /// <param name="prefabName">The creature's prefab name when it is known separately (used only if the name is a key
        /// this game cannot resolve); may be null.</param>
        internal static string CreatureToEnglish(string name, string prefabName)
        {
            string text = Clean(BossCatalog.CleanPrefabName(name));
            if (text.Length == 0)
                return string.Empty;

            if (IsKey(text))
            {
                string english = TryGameEnglish(text);
                if (english != null)
                    return english;

                string known = FromCreatureTable(BossCatalog.CleanPrefabName(prefabName));
                return known ?? FromWords(text, false);
            }

            string fromTable = FromCreatureTable(text);
            if (fromTable != null)
                return fromTable;

            if (LooksLikePrefabName(text))
                return FromWords(text, false);

            return text;
        }

        private static string FromCreatureTable(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return null;

            string name;
            if (CreatureByPrefab.TryGetValue(prefab, out name))
                return name;

            BossInfo boss;
            if (BossCatalog.TryGet(prefab, out boss))
                return boss.DisplayName;

            return null;
        }

        // ---------------------------------------------------------------- the game's own English text

        /// <summary>
        /// The game's English text for a key, or null when this game is not set to English or does not know the key.
        /// </summary>
        private static string TryGameEnglish(string key)
        {
            try
            {
                Localization localization = Localization.instance;
                if (localization == null)
                    return null;

                string language = localization.GetSelectedLanguage();
                if (!string.Equals(language, English, StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!string.Equals(_cacheLanguage, language, StringComparison.Ordinal))
                {
                    GameCache.Clear();
                    _cacheLanguage = language;
                }

                string cached;
                if (GameCache.TryGetValue(key, out cached))
                    return cached;

                string text = Clean(localization.Localize(key));

                // The game answers an unknown key with the key itself or with "[key]".
                bool usable = text.Length > 0 &&
                              text[0] != '$' &&
                              text[0] != '[' &&
                              !string.Equals(text, key, StringComparison.OrdinalIgnoreCase);

                cached = usable ? text : null;

                if (GameCache.Count < 512)
                    GameCache[key] = cached;

                return cached;
            }
            catch (Exception)
            {
                // Localization is not ready (or does not exist in this process): the key's own words are used instead.
                return null;
            }
        }

        // ---------------------------------------------------------------- building a name from the key's words

        private static bool IsKey(string text)
        {
            return text.Length > 1 && text[0] == '$';
        }

        /// <summary>
        /// A prefab name has no spaces and either uses underscores or runs words together ("SwordIron", "Bow_Huntsman").
        /// A one-word name ("Torch") or any name with a space ("Iron sword") is not one.
        /// </summary>
        private static bool LooksLikePrefabName(string text)
        {
            if (text.Length < 4)
                return false;

            bool hasInnerCapital = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '_')
                    return true;

                if (!char.IsLetterOrDigit(c))
                    return false;

                if (i > 0 && char.IsUpper(c) && !char.IsUpper(text[i - 1]))
                    hasInnerCapital = true;
            }

            return hasInnerCapital;
        }

        /// <summary>
        /// Builds a name from the words of a key or a prefab name.
        /// Items: "$item_sword_iron" -> "Iron sword"; "SwordIron" -> "Iron sword"; "$item_bow_huntsman" -> "Bow huntsman".
        /// Creatures: "$enemy_greydwarf_elite" -> "Greydwarf Elite"; "Skeleton_Poison" -> "Skeleton Poison" (no material
        /// reordering, every word capitalised, like the game's own creature names).
        /// </summary>
        /// <param name="text">The key or prefab name.</param>
        /// <param name="item">True for an item (weapon), false for a creature.</param>
        private static string FromWords(string text, bool item)
        {
            string s = text.Trim().TrimStart('$');

            if (item)
            {
                if (s.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(5);
            }
            else
            {
                foreach (string prefix in CreatureKeyPrefixes)
                {
                    if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        s = s.Substring(prefix.Length);
                        break;
                    }
                }
            }

            s = CamelBoundary.Replace(s, " ").Replace('_', ' ');

            List<string> words = new List<string>();
            foreach (string part in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Short all-capital words (an abbreviation) are kept as they are.
                bool abbreviation = part.Length <= 3 && part == part.ToUpperInvariant();
                words.Add(abbreviation ? part : part.ToLowerInvariant());
            }

            if (words.Count == 0)
                return string.Empty;

            // "sword iron" -> "iron sword" (items only: "Stone golem" must not become "Golem stone")
            if (item && words.Count >= 2 && IsMaterial(words[words.Count - 1]))
            {
                string material = words[words.Count - 1];
                words.RemoveAt(words.Count - 1);
                words.Insert(0, material);
            }

            StringBuilder sb = new StringBuilder(s.Length + 2);
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                string word = words[i];
                bool capitalise = !item || i == 0;
                if (capitalise && word.Length > 0 && word != word.ToUpperInvariant())
                    word = char.ToUpperInvariant(word[0]) + word.Substring(1);

                sb.Append(word);
            }

            if (item && sb.Length > 0)
                sb[0] = char.ToUpperInvariant(sb[0]);

            return sb.ToString();
        }

        private static bool IsMaterial(string word)
        {
            foreach (string material in MaterialWords)
            {
                if (string.Equals(word, material, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return AnyTag.Replace(value, string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}
