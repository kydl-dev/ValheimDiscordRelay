namespace ValheimDiscordRelay;

/// <summary>Cuts text to a length limit without breaking an emoji (a surrogate pair) in half. Pure logic (unit tested).</summary>
internal static class TextLimit
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it fits; otherwise the first characters followed by an ellipsis, so
    /// the result is at most <paramref name="maxChars"/> characters long. A pair of characters that form one emoji is
    /// never split: if the cut would fall between them, the first half is dropped too (an invalid string makes Discord
    /// answer HTTP 400).
    /// </summary>
    /// <param name="value">The text; null is returned as null.</param>
    /// <param name="maxChars">The most characters the result may have (at least 2).</param>
    internal static string WithEllipsis(string value, int maxChars)
    {
        if (value == null || value.Length <= maxChars)
            return value;

        int keep = maxChars - 1;
        if (keep > 0 && char.IsHighSurrogate(value[keep - 1]))
            keep--;

        return value.Substring(0, keep) + "\u2026";
    }
}
