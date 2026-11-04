using NUnit.Framework;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Tests.Unit;

[TestFixture]
public class DiscordNameFallbackTests
{
    [Test]
    public void RefusedName_IsReplacedAndKeptInTheText()
    {
        bool ok = DiscordNameFallback.TryBuild("Discord Dave", "hello", out string name, out string content);

        Assert.That(ok, Is.True);
        Assert.That(name, Is.EqualTo("Valheim Player"));
        Assert.That(content, Is.EqualTo("**Discord Dave:** hello"));
    }

    [Test]
    public void NameIsTrimmed()
    {
        DiscordNameFallback.TryBuild("  Clyde  ", "hi", out _, out string content);

        Assert.That(content, Is.EqualTo("**Clyde:** hi"));
    }

    [Test]
    public void NullContent_GivesOnlyTheName()
    {
        bool ok = DiscordNameFallback.TryBuild("Clyde", null, out _, out string content);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("**Clyde:** "));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Valheim Player")]
    public void NoSecondTry_WhenThereIsNoName_OrTheNeutralNameWasAlreadyUsed(string username)
    {
        bool ok = DiscordNameFallback.TryBuild(username, "hello", out string name, out string content);

        Assert.That(ok, Is.False);
        Assert.That(name, Is.EqualTo("Valheim Player"));
        Assert.That(content, Is.EqualTo("hello"));
    }

    [Test]
    public void LongText_IsCutToDiscordsLimit()
    {
        DiscordNameFallback.TryBuild("Clyde", new string('x', 3000), out _, out string content);

        Assert.That(content.Length, Is.EqualTo(2000));
        Assert.That(content, Does.StartWith("**Clyde:** x"));
        Assert.That(content, Does.EndWith("\u2026"));
    }

    [Test]
    public void TextExactlyAtTheLimit_IsNotCut()
    {
        string body = new string('x', 2000 - "**Clyde:** ".Length);
        DiscordNameFallback.TryBuild("Clyde", body, out _, out string content);

        Assert.That(content.Length, Is.EqualTo(2000));
        Assert.That(content, Does.EndWith("x"));
    }
}
