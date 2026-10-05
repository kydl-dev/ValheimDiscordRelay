using System;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// The messages shown in the boss death embed. One is picked at random for each kill: from the solo list when
    /// one player dealt all the damage, from the party list when two or more did.
    ///
    /// To add, remove or reword a quote, just edit the lists below. Placeholders:
    ///   {Boss}   the boss's name
    ///   {Player} the top damage dealer's name (party quotes)
    ///   {Damage} solo quotes: the damage the player dealt; party quotes: the top damage dealer's damage
    /// Use \n\n for a blank line, and \u2019 / \u2018 / \u2014 for ' ' and the long dash (keeps the file encoding-proof).
    /// </summary>
    internal static class BossQuotes
    {
        private static readonly string[] Solo =
        {
            "{Boss} has been defeated! Turns out it was no match for one moderately competent Viking.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "You killed {Boss} alone? Who needs friends when you have questionable life choices?\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "Solo victory! {Boss} never stood a chance against that much stubbornness.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "Well played. {Boss} has been slain, and your ego has gained +10.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "{Boss} is dead. Please remember to thank nobody for the assistance.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "A solo kill?! Incredible. We\u2019ll notify the bards immediately.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "You actually killed it alone. I had money on \u2018dies horribly.\u2019\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "{Boss} has fallen! Unfortunately, so has your excuse for not helping the team.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "Solo boss kill unlocked. Achievement: Couldn\u2019t Find Anyone to Help.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "Against all odds, and apparently against all common sense, you killed {Boss}.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it.",
            "{Boss}, a boss? Nah. This lone wolf made it look easy.\n\nGood job, {Player}! You've somehow managed to deal {Damage} damage to it."
        };

        private static readonly string[] Party =
        {
            "{Boss} has fallen! MVP goes to {Player} with a brutal {Damage} damage. The rest of you were there too, apparently.",
            "Victory! {Boss} is dead, thanks largely to {Player}, who dealt {Damage} damage while everyone else provided moral support.",
            "The Vikings have prevailed! Special mention to {Player} for putting {Damage} damage into {Boss}. Please try to contain your ego.",
            "{Boss} has been slain! {Player} led the damage charts with {Damage} damage. Everyone else: thanks for showing up.",
            "Another boss bites the dust! {Player} contributed a mighty {Damage} damage. The rest of the party contributed\u2026 atmosphere.",
            "Victory! {Boss} is down, and {Player} is officially the party\u2019s designated damage goblin with {Damage} damage.",
            "Well fought, Vikings! {Player} dealt {Damage} damage to {Boss}. We assume the rest of you were busy with important Viking business.",
            "{Boss} has fallen! {Player} topped the damage charts with {Damage}. Somewhere, the other Vikings are furiously checking their excuses.",
            "Boss defeated! {Player} delivered {Damage} damage and carried the party straight into Valhalla. Please collect your complimentary ego boost at the door.",
            "{Boss} is dead! Congratulations to everyone involved \u2014 especially {Player}, who apparently decided {Damage} damage was a reasonable amount of violence.",
            "You are stronger together, {Boss} had no chance against such a strong team. {Player} managed to do {Damage} to {Boss}."
        };

        // One pass over the template, so a player name that happens to contain "{Damage}" is never substituted again.
        private static readonly Regex Token = new(@"\{(Boss|Player|Damage)\}", RegexOptions.CultureInvariant);

        private static readonly Random Rng = new(Guid.NewGuid().GetHashCode());
        private static readonly object RngLock = new();
        private static int _lastSolo = -1;
        private static int _lastParty = -1;

        /// <summary>
        /// A random solo-kill message.
        /// </summary>
        /// <param name="boss">Boss name, already safe for Discord markdown.</param>
        /// <param name="player"></param>
        /// <param name="damage">The damage the player dealt, formatted.</param>
        internal static string PickSolo(string boss, string player, string damage)
        {
            string template;
            lock (RngLock)
                template = Pick(Solo, ref _lastSolo);

            return Fill(template, boss, player, damage);
        }

        /// <summary>
        /// A random party-kill message.
        /// </summary>
        /// <param name="boss">Boss name, already safe for Discord markdown.</param>
        /// <param name="player">Top damage dealer's name, already safe for Discord markdown.</param>
        /// <param name="damage">The top damage dealer's damage, formatted.</param>
        internal static string PickParty(string boss, string player, string damage)
        {
            string template;
            lock (RngLock)
                template = Pick(Party, ref _lastParty);

            return Fill(template, boss, player, damage);
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

        private static string Fill(string template, string boss, string player, string damage)
        {
            return Token.Replace(template, delegate (Match match)
            {
                switch (match.Groups[1].Value)
                {
                    case "Boss": return boss;
                    case "Player": return player;
                    default: return damage;
                }
            });
        }
    }
}
