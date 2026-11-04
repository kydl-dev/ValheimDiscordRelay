using System;
using System.Collections.Generic;
using NUnit.Framework;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Tests.Unit;

/// <summary>The per-player chat limit: a burst of 10, then one line per second; the drop warning at most every 30 s.</summary>
[TestFixture]
public class ChatRateLimiterTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    readonly List<string> _warnings = new List<string>();

    [SetUp]
    public void SetUp()
    {
        ChatRateLimiter.Reset();
        _warnings.Clear();
    }

    bool Allow(long sender, double seconds, string name = "Dave")
    {
        return ChatRateLimiter.Allow(sender, name, T0.AddSeconds(seconds), _warnings.Add);
    }

    [Test]
    public void ABurstOfTen_IsAllowed_TheEleventhIsNot()
    {
        for (int i = 0; i < 10; i++)
            Assert.That(Allow(1, 0), Is.True, "line " + (i + 1));
        Assert.That(Allow(1, 0), Is.False);
    }

    [Test]
    public void OneLinePerSecond_IsRefilled()
    {
        for (int i = 0; i < 10; i++) Allow(1, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Allow(1, 0.5), Is.False, "half a token is not enough");
            Assert.That(Allow(1, 1.0), Is.True, "a full token after one second");
            Assert.That(Allow(1, 1.0), Is.False, "and it is used up");
        }
    }

    [Test]
    public void TheBucketNeverHoldsMoreThanTheBurst()
    {
        Allow(1, 0);
        for (int i = 0; i < 9; i++) Allow(1, 0);          // empty
        int allowed = 0;
        for (int i = 0; i < 20; i++) if (Allow(1, 1000)) allowed++;   // a long pause refills to 10, not more
        Assert.That(allowed, Is.EqualTo(10));
    }

    [Test]
    public void PlayersHaveTheirOwnBucket()
    {
        for (int i = 0; i < 10; i++) Allow(1, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Allow(1, 0), Is.False);
            Assert.That(Allow(2, 0), Is.True);
        }
    }

    [Test]
    public void Reset_ForgetsEveryBucket()
    {
        for (int i = 0; i < 10; i++) Allow(1, 0);
        ChatRateLimiter.Reset();
        Assert.That(Allow(1, 0), Is.True);
    }

    [Test]
    public void ClockGoingBackwards_NeverTakesTokensAway()
    {
        Allow(1, 100);
        Assert.That(Allow(1, 0), Is.True);
    }

    [Test]
    public void DroppedLines_WarnOncePer30Seconds_WithNameAndUid()
    {
        for (int i = 0; i < 10; i++) Allow(77, 0, "Dave");
        Allow(77, 0, "Dave");
        Allow(77, 0, "Dave");
        Assert.That(_warnings, Has.Count.EqualTo(1));
        Assert.That(_warnings[0], Is.EqualTo("Chat relay from 'Dave' (77) is sending too fast; extra lines are not sent to Discord."));
        for (int i = 0; i < 29; i++) Allow(77, 0.001 + i * 0.0001);   // still under 30 s since the first warning
        Assert.That(_warnings, Has.Count.EqualTo(1));
        for (int i = 0; i < 10; i++) Allow(77, 30.0);                  // bucket empties again exactly 30 s later
        Allow(77, 30.0);
        Assert.That(_warnings, Has.Count.EqualTo(2), "a second warning after 30 s");
    }

    [Test]
    public void AllowedLines_NeverWarn()
    {
        for (int i = 0; i < 10; i++) Allow(5, 0);
        Assert.That(_warnings, Is.Empty);
    }

    [Test]
    public void AfterTheTableIsFull_StartsOverInsteadOfGrowing()
    {
        for (long id = 1; id <= 128; id++) Allow(id, 0);
        for (int i = 0; i < 10; i++) Allow(1, 0);                       // player 1 is empty now
        Allow(500, 0);                                                  // the 129th player clears the table
        Assert.That(Allow(1, 0), Is.True, "player 1 starts with a full bucket again");
    }
}
