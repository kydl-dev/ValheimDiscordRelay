using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimDiscordRelay;

/// <summary>
/// A small "already handled" memory for the server: the keys of boss objects whose death was already announced or
/// counted, so the same death reported twice (for example again after a server restart) is not handled twice.
///
/// It keeps the newest <c>capacity</c> keys, remembers when each one was added, and gives the entries back oldest first
/// so the owner can save them to disk and load them again. A key older than <c>maxAge</c> is not loaded again:
/// a repeated report of a death only makes sense shortly after the death, and a key must not silence a boss for ever
/// when a world is restored from an older backup (the boss object then has the same key again).
///
/// Not thread-safe: the owner locks around every call, as it does for its other data.
/// </summary>
internal sealed class RememberedKeys
{
    /// <summary>The longest key accepted when loading from disk.</summary>
    private const int MaxKeyChars = 128;

    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<KeyValuePair<string, DateTime>> _order = new Queue<KeyValuePair<string, DateTime>>();

    /// <param name="capacity">How many keys to keep; the oldest is forgotten first.</param>
    /// <param name="maxAge">Keys older than this are not loaded back from disk.</param>
    internal RememberedKeys(int capacity, TimeSpan maxAge)
    {
        _capacity = Math.Max(1, capacity);
        _maxAge = maxAge;
    }

    /// <summary>The keys in memory, oldest first, each with the UTC time it was added.</summary>
    internal IEnumerable<KeyValuePair<string, DateTime>> Entries
    {
        get { return _order; }
    }

    internal bool Contains(string key)
    {
        return key != null && _keys.Contains(key);
    }

    /// <summary>Remembers a key as of now.</summary>
    /// <returns>True if the key was new; false if it was already remembered (the caller should then do nothing).</returns>
    internal bool Add(string key)
    {
        return Add(key, DateTime.UtcNow);
    }

    private bool Add(string key, DateTime whenUtc)
    {
        if (string.IsNullOrEmpty(key) || !_keys.Add(key))
            return false;

        _order.Enqueue(new KeyValuePair<string, DateTime>(key, whenUtc));
        while (_order.Count > _capacity)
            _keys.Remove(_order.Dequeue().Key);

        return true;
    }

    internal void Clear()
    {
        _keys.Clear();
        _order.Clear();
    }

    /// <summary>
    /// Loads one saved entry. Call it for the saved entries in file order (oldest first).
    /// </summary>
    /// <param name="key">The key as saved.</param>
    /// <param name="timeText">The time the key was added, in the round-trip ("O") format written by <see cref="FormatTime"/>.</param>
    /// <returns>True if the entry was loaded; false if it is empty, too long, unreadable, too old, or already known.</returns>
    internal bool TryLoad(string key, string timeText)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyChars)
            return false;

        DateTime when;
        if (!DateTime.TryParse(
                timeText == null ? null : timeText.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out when))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (when > now)
            when = now; // the clock went back: count the entry as added now

        if (now - when > _maxAge)
            return false;

        return Add(key, when);
    }

    /// <summary>The text written to disk for a time, read back by <see cref="TryLoad"/>.</summary>
    internal static string FormatTime(DateTime whenUtc)
    {
        return whenUtc.ToString("O", CultureInfo.InvariantCulture);
    }
}
