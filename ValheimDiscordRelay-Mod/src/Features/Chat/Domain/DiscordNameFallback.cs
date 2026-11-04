using System;

namespace ValheimDiscordRelay.Server;

/// <summary>
/// Discord answers HTTP 400 when a webhook "username" contains the words "discord" or "clyde" (a player may be called
/// that). Instead of losing the chat line, it is sent once more under a neutral sender name, with the real player name
/// put in front of the text. Pure logic, no game types (unit tested).
/// </summary>
internal static class DiscordNameFallback
{
    /// <summary>The sender name used for the second try.</summary>
    internal const string FallbackName = "Valheim Player";

    /// <summary>Discord's limit for the text of one message.</summary>
    internal const int MaxContentChars = 2000;

    /// <summary>
    /// Builds the second try for a chat message Discord refused.
    /// </summary>
    /// <param name="username">The sender name that was refused.</param>
    /// <param name="content">The message text.</param>
    /// <param name="newUsername">The neutral sender name.</param>
    /// <param name="newContent">The text with the player name in front, cut to Discord's limit.</param>
    /// <returns>False when a second try makes no sense (no name, or the neutral name was already used).</returns>
    internal static bool TryBuild(string username, string content, out string newUsername, out string newContent)
    {
        newUsername = FallbackName;
        newContent = content;

        if (string.IsNullOrWhiteSpace(username) || username == FallbackName)
            return false;

        string text = "**" + username.Trim() + ":** " + (content ?? string.Empty);
        if (text.Length > MaxContentChars)
            text = text.Substring(0, MaxContentChars - 1) + "\u2026";

        newContent = text;
        return true;
    }
}
