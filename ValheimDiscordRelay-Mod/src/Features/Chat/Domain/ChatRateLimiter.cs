using System;
using System.Collections.Generic;

namespace ValheimDiscordRelay.Server;

internal static class ChatRateLimiter
{
    /// <summary>A player may send a burst of this many chat lines to Discord ...</summary>
    private const double ChatBurst = 10.0;

    /// <summary>... and then this many per second on average (normal typing never gets near it).</summary>
    private const double ChatRefillPerSecond = 1.0;

    private const int MaxRememberedChatPeers = 128;

    /// <summary>Forgets every player's bucket (server shutdown).</summary>
    internal static void Reset()
    {
        ChatBuckets.Clear();
    }

    private sealed class ChatBucket
    {
        public double Tokens = ChatBurst;
        public DateTime LastUtc = DateTime.UtcNow;
        public DateTime LastWarnUtc = DateTime.MinValue;
    }

    // Only touched from the game's main thread (RPC handlers), so no lock is needed.
    private static readonly Dictionary<long, ChatBucket> ChatBuckets = new Dictionary<long, ChatBucket>();

    /// <summary>
    /// Token-bucket limit per connected player (RPC path only; the host's own chat is not limited).
    /// Dropped lines are logged at most once every 30 seconds per player.
    /// </summary>
    /// <param name="sender">The sending peer's uid.</param>
    /// <param name="playerName">The sender's character name, for the log line.</param>
    /// <returns>True if this chat line may be relayed.</returns>
    internal static bool Allow(long sender, string playerName)
    {
        return Allow(sender, playerName, DateTime.UtcNow, RelayDiagnostics.Warning);
    }

    /// <summary>The same with the clock and the warning sink passed in (unit tests).</summary>
    internal static bool Allow(long sender, string playerName, DateTime now, Action<string> warn)
    {

        ChatBucket bucket;
        if (!ChatBuckets.TryGetValue(sender, out bucket))
        {
            // Players come and go all week: start over rather than let the table grow without limit.
            if (ChatBuckets.Count >= MaxRememberedChatPeers)
                ChatBuckets.Clear();

            bucket = new ChatBucket();
            bucket.LastUtc = now;
            ChatBuckets[sender] = bucket;
        }

        double elapsed = Math.Max(0.0, (now - bucket.LastUtc).TotalSeconds);
        bucket.LastUtc = now;
        bucket.Tokens = Math.Min(ChatBurst, bucket.Tokens + elapsed * ChatRefillPerSecond);

        if (bucket.Tokens >= 1.0)
        {
            bucket.Tokens -= 1.0;
            return true;
        }

        if ((now - bucket.LastWarnUtc).TotalSeconds >= 30.0)
        {
            bucket.LastWarnUtc = now;
            warn("Chat relay from '" + playerName + "' (" + sender + ") is sending too fast; extra lines are not sent to Discord.");
        }

        return false;
    }
}
