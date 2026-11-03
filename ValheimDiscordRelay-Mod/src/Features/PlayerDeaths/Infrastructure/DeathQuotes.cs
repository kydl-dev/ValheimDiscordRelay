using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// The messages shown when the local player dies. One is picked at random for each death, never the same
    /// one twice in a row within a category. Categories:
    ///   - killed by an animal with no stars, one star, or two-or-more stars (three separate lists)
    ///   - killed by any other (non-animal) enemy, including other players
    ///   - environmental deaths (drowning, burning, freezing, falling, ...)
    ///
    /// To add, remove or reword a quote, just edit the lists below. Placeholders:
    ///   {Player.name}    the player's name
    ///   {Creature.name}  the killer's name. Animals are written in lower case mid-sentence and capitalised at the
    ///                    start of a sentence; every other enemy keeps the name Valheim shows (e.g. "Greydwarf Brute")
    ///   {level}          the killer's stars, with a leading space: " (2 stars)" in game chat (the game font has no
    ///                    emoji) and a row of star emoji in Discord. Empty for a creature with no stars.
    ///   {star}           a single star: "star" in game chat and a star emoji in Discord
    ///   {Cause}          environmental deaths only: what killed the player, e.g. "drowning" (see DescribeCause in
    ///                    DeathMessageBuilder.cs)
    /// Use \u2019 / \u2018 / \u2014 for ' ' and the long dash, and \u00e9 and \u00e5 for e-acute and a-ring (keeps the file encoding-proof).
    /// </summary>
    internal static class DeathQuotes
    {
        /// <summary>
        /// Prefab names counted as animals (compared ignoring case, without the "(Clone)" suffix). Everything else
        /// that kills the player uses the non-animal list. Add a prefab name here to make another creature an animal.
        /// </summary>
        private static readonly HashSet<string> AnimalPrefabs = new(StringComparer.OrdinalIgnoreCase)
        {
            "Boar",
            "Neck",
            "Bjorn",       // the bear
            "Serpent",
            "Leech",
            "Wolf",
            "Bat",
            "Bat_swamp",
            "Lox",
            "Deathsquito",
            "Volture",
            "Asksvin",
            "Moose"
        };

        private static readonly string[] AnimalNoStar =
        {
            "{Player.name} was slain by a completely ordinary {Creature.name}. This is going to be difficult to explain.",
            "{Creature.name} has turned {Player.name} into forest food. Circle of life, Viking edition.",
            "{Creature.name} has claimed {Player.name}. You picked a fight with wildlife. Wildlife won.",
            "{Creature.name}, protecting its territory. {Player.name}, apparently part of the problem.",
            "{Player.name} has been eaten by a {Creature.name}. No stars. No excuses.",
            "A humble {Creature.name} has slain {Player.name}. Nature is healing.",
            "{Player.name} vs. an ordinary {Creature.name}, the {Creature.name} won. We may need to discuss your combat strategy.",
            "{Player.name} has fallen to a {Creature.name}. Not a particularly impressive {Creature.name}, either.",
            "The mighty Viking {Player.name} has been defeated by a regular {Creature.name}. Sk\u00e5l!",
            "{Creature.name}, 1. {Player.name}, 0. The creature didn't even have the decency to be special."
        };

        private static readonly string[] AnimalOneStar =
        {
            "{Player.name} was slain by a {Creature.name}{level}. Finally, an enemy with a r\u00e9sum\u00e9.",
            "A {Creature.name}{level} has defeated {Player.name}. To be fair, it had an entire extra star.",
            "{Player.name} picked a fight with a 1-star {Creature.name}. The animal had a family. What did you have?",
            "{Creature.name}{level} has claimed {Player.name}. Apparently that one extra star made all the difference.",
            "{Player.name} was killed by a {Creature.name}{level}. Congratulations to the {Creature.name}{level} on its impressive career advancement.",
            "{Creature.name}{level} has claimed another Viking. Somewhere, its proud mother is telling all the other animals.",
            "{Player.name} underestimated the {star} on that {Creature.name}{level}. The {star} did not underestimate {Player.name}.",
            "{Player.name} has been promoted to dinner by a {Creature.name}{level}.",
            "A {Creature.name}{level} just turned {Player.name} into Viking mince. Stellar performance.",
            "{Creature.name}{level} defeated {Player.name}. The star was apparently worth more than all that expensive Viking gear."
        };

        private static readonly string[] AnimalMultiStar =
        {
            "{Player.name} was slain by a {Creature.name}{level}. In fairness, that thing was basically a natural disaster with legs.",
            "{Creature.name}{level} has claimed {Player.name}. You really should have read the warning label.",
            "{Player.name} has fallen to a {Creature.name}{level}. That's not a normal animal. That's an angry tax collector from Valhalla.",
            "A {Creature.name}{level} has eaten {Player.name}. At least you were defeated by something genuinely terrifying.",
            "{Player.name} challenged a {Creature.name}{level} and discovered why stars are generally considered important.",
            "{Player.name} was slain by mama {Creature.name}{level}. Her babies are safe, and you are not.",
            "{Player.name} has been slain by a very protective mother {Creature.name}{level}. She had nothing left to lose.",
            "A heavily starred {Creature.name}{level} has claimed another Viking. {Player.name} fought bravely. Briefly.",
            "{Player.name} has fallen to {Creature.name}{level}. That's not a death. That's an unfortunate wildlife encounter.",
            "{Creature.name}{level} defeated {Player.name}. The stars aligned. Unfortunately, they aligned against you."
        };

        // Written without an article ("slain by {Creature.name}") because the killer can also be another player.
        // The last entry is the original creature message.
        private static readonly string[] NonAnimal =
        {
            "{Player.name} has been slain by {Creature.name}{level}. Somewhere in Valheim, an enemy is feeling very proud of itself.",
            "{Creature.name}{level} has defeated {Player.name}. Another great Viking adventure ends in administrative failure.",
            "RIP {Player.name}. Killed by {Creature.name}{level}. Your gear will be missed almost as much as your competence.",
            "{Player.name} has fallen to {Creature.name}{level}. The Valheim insurance company has denied your claim.",
            "{Creature.name}{level} has claimed {Player.name}. In other news, local Viking discovers that enemies can, in fact, hurt.",
            "{Player.name} was slain by {Creature.name}{level}. It wasn't even a boss. Where's the dignity?",
            "{Creature.name}{level} defeated {Player.name}. The battle lasted longer in {Player.name}'s imagination.",
            "{Player.name} has met their untimely demise at the hands of {Creature.name}{level}. Please leave your valuables where they are.",
            "Another Viking falls! {Player.name} was defeated by {Creature.name}{level}. The enemies are becoming alarmingly competent.",
            "{Creature.name}{level} has slain {Player.name}. Congratulations, {Creature.name}. You have officially ruined someone's evening.",
            "{Player.name} got killed by a {Creature.name}{level}. The {Creature.name} is dancing on {Player.name}'s tombstone."
        };

        // The last entry is the original environmental message.
        private static readonly string[] Environmental =
        {
            "{Player.name} has been defeated by the elements. Nature didn't even need to send a creature.",
            "{Player.name} has discovered that the environment is, in fact, hostile.",
            "The world itself has claimed {Player.name}. That's one way to lose a fight.",
            "{Player.name} has discovered one of Valheim's many creative ways to die, {Cause}.",
            "{Player.name} has perished due to environmental circumstances. Skill issue.",
            "No enemy in sight. Just {Player.name} losing a fight against the environment.",
            "{Player.name} has been claimed by the elements. Odin will never know what happened here.",
            "{Player.name} has discovered one of Valheim's many creative ways to die.",
            "The environment has defeated {Player.name}. Nature is indeed very powerful.",
            "{Player.name} has died to the forces of nature. The wilderness remains undefeated.",
            "{Player.name} learned the hard way why you can't fight nature."
        };

        // One pass over the template, so a player name that happens to contain "{Cause}" is never substituted again.
        private static readonly Regex Token = new(
            @"\{(Player\.name|Creature\.name|level|star|Cause)\}", RegexOptions.CultureInvariant);

        private static readonly Random Rng = new(Guid.NewGuid().GetHashCode());
        private static readonly object RngLock = new();

        private static int _lastAnimalNoStar = -1;
        private static int _lastAnimalOneStar = -1;
        private static int _lastAnimalMultiStar = -1;
        private static int _lastNonAnimal = -1;
        private static int _lastEnvironmental = -1;

        /// <summary>
        /// True if the prefab name belongs to an animal. Valheim's "(Clone)" suffix is ignored.
        /// </summary>
        /// <param name="prefabName">The attacker's prefab name, or null if unknown.</param>
        internal static bool IsAnimalPrefab(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return false;

            string name = BossCatalog.CleanPrefabName(prefabName);
            return AnimalPrefabs.Contains(name);
        }

        /// <summary>
        /// A random death message for a killer that is not a boss.
        /// </summary>
        /// <param name="player">The player's name, already safe for Discord markdown.</param>
        /// <param name="attacker">The creature (or player) that killed the player.</param>
        /// <returns>The message in in-game and Discord form.</returns>
        internal static DeathMessage PickCreature(string player, AttackerInfo attacker)
        {
            string template;
            lock (RngLock)
            {
                if (!attacker.IsAnimal)
                    template = Pick(NonAnimal, ref _lastNonAnimal);
                else if (attacker.StarCount <= 0)
                    template = Pick(AnimalNoStar, ref _lastAnimalNoStar);
                else if (attacker.StarCount == 1)
                    template = Pick(AnimalOneStar, ref _lastAnimalOneStar);
                else
                    template = Pick(AnimalMultiStar, ref _lastAnimalMultiStar);
            }

            return new DeathMessage(
                Fill(template, player, attacker, null, false),
                Fill(template, player, attacker, null, true));
        }

        /// <summary>
        /// A random environmental death message.
        /// </summary>
        /// <param name="player">The player's name, already safe for Discord markdown.</param>
        /// <param name="cause">What killed the player, as a short lower-case phrase (fills {Cause}).</param>
        internal static string PickEnvironmental(string player, string cause)
        {
            string template;
            lock (RngLock)
                template = Pick(Environmental, ref _lastEnvironmental);

            return Fill(template, player, null, cause, false);
        }

        /// <summary>
        /// Picks a random entry, never the same one twice in a row (when there is more than one to choose from).
        /// </summary>
        private static string Pick(string[] quotes, ref int last)
        {
            int index = Rng.Next(quotes.Length);
            if (quotes.Length > 1 && index == last)
                index = (index + 1 + Rng.Next(quotes.Length - 1)) % quotes.Length;

            last = index;
            return quotes[index];
        }

        private static string Fill(string template, string player, AttackerInfo attacker, string cause, bool discord)
        {
            return Token.Replace(template, delegate (Match match)
            {
                switch (match.Groups[1].Value)
                {
                    case "Player.name":
                        return player;
                    case "Creature.name":
                        return CreatureName(attacker, StartsSentence(template, match.Index));
                    case "level":
                        return Level(attacker, discord);
                    case "star":
                        return discord ? "\u2B50" : "star";
                    default:
                        return string.IsNullOrWhiteSpace(cause) ? "the elements" : cause;
                }
            });
        }

        private static string CreatureName(AttackerInfo attacker, bool sentenceStart)
        {
            if (attacker == null)
                return "creature";

            // Only animals are re-cased; other enemies and players keep the name as Valheim shows it.
            if (!attacker.IsAnimal)
                return attacker.Name;

            if (!sentenceStart)
                return attacker.Name.ToLowerInvariant();

            string name = attacker.Name;
            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
        }

        private static string Level(AttackerInfo attacker, bool discord)
        {
            if (attacker == null || attacker.StarCount <= 0)
                return string.Empty;

            if (discord)
                return " " + attacker.BuildStars("\u2B50");

            int stars = attacker.StarCount;
            return " (" + stars + (stars == 1 ? " star)" : " stars)");
        }

        /// <summary>
        /// True when the token at <paramref name="index"/> opens a sentence (start of the message, or after . ! ?).
        /// </summary>
        private static bool StartsSentence(string template, int index)
        {
            int i = index - 1;
            while (i >= 0 && char.IsWhiteSpace(template[i]))
                i--;

            return i < 0 || template[i] == '.' || template[i] == '!' || template[i] == '?';
        }
    }
}
