using System;
using NUnit.Framework;
using ValheimDiscordRelay;

namespace ValheimDiscordRelay.Tests.Unit;

/// <summary>The boss list, the multi-phase boss rules and the text helpers of the boss announcements.</summary>
[TestFixture]
public class BossCatalogTests
{
    private static readonly object[] Bosses =
    {
        new object[] { "Eikthyr", "Eikthyr", "https://valheim.weirdgloop.org/images/Boss1Eikthyr_unlocked.jpg?44822", "https://www.valheim.tools/renders/creatures/Eikthyr.png" },
        new object[] { "gd_king", "The Elder", "https://valheim.weirdgloop.org/images/Boss2Elder_unlocked.jpg?9180f", "https://www.valheim.tools/renders/creatures/gd_king.png" },
        new object[] { "Bonemass", "Bonemass", "https://valheim.weirdgloop.org/images/Boss3Bonemass_unlocked.jpg?44822", "https://www.valheim.tools/renders/creatures/Bonemass.png" },
        new object[] { "Dragon", "Moder", "https://valheim.weirdgloop.org/images/Boss4Moder_unlocked.jpg?44822", "https://www.valheim.tools/renders/creatures/Dragon.png" },
        new object[] { "GoblinKing", "Yagluth", "https://valheim.weirdgloop.org/images/Boss5Yagluth_unlocked.jpg?9180f", "https://www.valheim.tools/renders/creatures/GoblinKing.png" },
        new object[] { "SeekerQueen", "The Queen", "https://valheim.weirdgloop.org/images/Boss6Queen_unlocked.jpg?9180f", "https://www.valheim.tools/renders/creatures/SeekerQueen.png" },
        new object[] { "Fader", "Fader", "https://valheim.weirdgloop.org/images/Boss7Fader_unlocked.jpg?9180f", "https://www.valheim.tools/renders/creatures/Fader.png" },
        new object[] { "FrozenKing_p3", "Kall Fimbulbringer", "https://valheim.weirdgloop.org/images/Boss8FrozenKing_unlocked.jpg?9180f", "https://www.valheim.tools/renders/creatures/FrozenKing.png" },
    };

    [TestCaseSource(nameof(Bosses))]
    public void EveryBoss_IsKnown_WithItsNameAndPictures(string prefab, string display, string avatar, string photo)
    {
        BossInfo info;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BossCatalog.TryGet(prefab, out info), Is.True);
            Assert.That(info.Prefab, Is.EqualTo(prefab));
            Assert.That(info.DisplayName, Is.EqualTo(display));
            Assert.That(info.AvatarUrl, Is.EqualTo(avatar));
            Assert.That(info.PhotoUrl, Is.EqualTo(photo));
        }
    }

    [TestCase("Eikthyr(Clone)")]
    [TestCase("  eikthyr  ")]
    [TestCase("EIKTHYR (clone)")]
    public void TryGet_IgnoresCloneSuffixCaseAndBlanks(string prefab)
    {
        BossInfo info;
        Assert.That(BossCatalog.TryGet(prefab, out info), Is.True);
        Assert.That(info.DisplayName, Is.EqualTo("Eikthyr"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Boar")]
    [TestCase("FrozenKing_p1")]
    public void TryGet_UnknownOrEmpty_IsFalse(string prefab)
    {
        BossInfo info;
        Assert.That(BossCatalog.TryGet(prefab, out info), Is.False);
        Assert.That(info, Is.Null);
    }

    [TestCase("FrozenKing_p1", true)]
    [TestCase("FrozenKing_p2(Clone)", true)]
    [TestCase("frozenking", true)]
    [TestCase("FrozenKing_p3", false)]
    [TestCase("frozenking_P3(Clone)", false)]
    [TestCase("Eikthyr", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("  ", false)]
    public void IsIntermediatePhase_OnlyTheEarlyPhasesOfAMultiPhaseBoss(string prefab, bool expected)
    {
        Assert.That(BossCatalog.IsIntermediatePhase(prefab), Is.EqualTo(expected));
    }

    [TestCase("FrozenKing_p1", "FrozenKing_p3(Clone)", true)]
    [TestCase("frozenking_p2", "FROZENKING_P1", true)]
    [TestCase("FrozenKing_p1", "Eikthyr", false)]
    [TestCase("Eikthyr", "FrozenKing_p1", false)]
    [TestCase("Eikthyr", "Eikthyr", false)]
    [TestCase(null, "FrozenKing_p1", false)]
    [TestCase("FrozenKing_p1", null, false)]
    [TestCase(" ", "FrozenKing_p1", false)]
    [TestCase("FrozenKing_p1", " ", false)]
    public void IsSamePhaseFamily_BothPhasesOfTheSameBoss(string a, string b, bool expected)
    {
        Assert.That(BossCatalog.IsSamePhaseFamily(a, b), Is.EqualTo(expected));
    }

    [TestCase("FrozenKing_p2(Clone)", "obj-7", "fight:frozenking")]
    [TestCase("FrozenKing_p3", "obj-7", "fight:frozenking")]
    [TestCase("Eikthyr", "obj-7", "obj-7")]
    [TestCase(null, "obj-7", "obj-7")]
    [TestCase("", "obj-7", "obj-7")]
    [TestCase("  ", "obj-7", "obj-7")]
    public void InstanceKey_AllPhasesShareOneKey_OtherBossesKeepTheirObjectKey(string prefab, string objectKey, string expected)
    {
        Assert.That(BossCatalog.InstanceKey(prefab, objectKey), Is.EqualTo(expected));
    }

    [TestCase("Kall Fimbulbringer", "k1", "fight:frozenking")]
    [TestCase("kall fimbulbringer", "k1", "fight:frozenking")]
    [TestCase("<color=#fff>Kall Fimbulbringer</color>", "k1", "fight:frozenking")]
    [TestCase("Eikthyr", "k1", "k1")]
    [TestCase("Kall", "k1", "k1")]
    [TestCase("", "k1", "k1")]
    [TestCase(null, "k1", "k1")]
    [TestCase("  ", "k1", "k1")]
    [TestCase("<b></b>", "k1", "k1")]
    public void InstanceKeyForName_FindsTheMultiPhaseBossByItsDisplayName(string name, string objectKey, string expected)
    {
        Assert.That(BossCatalog.InstanceKeyForName(name, objectKey), Is.EqualTo(expected));
    }

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("Eikthyr", "Eikthyr")]
    [TestCase("Eikthyr(Clone)", "Eikthyr")]
    [TestCase("  Eikthyr (CLONE)", "Eikthyr")]
    [TestCase("(Clone)", "")]
    [TestCase("  x  ", "x")]
    public void CleanPrefabName_RemovesTheCloneSuffixAndBlanks(string input, string expected)
    {
        Assert.That(BossCatalog.CleanPrefabName(input), Is.EqualTo(expected));
    }

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("<b>Dave</b>", "Dave")]
    [TestCase("<color=#ff0000>Dave</color> the <i>Bold</i>", "Dave the Bold")]
    [TestCase("a\r\nb", "a  b")]
    [TestCase("a\nb", "a b")]
    [TestCase("  x ", "x")]
    [TestCase("a<b", "a<b")]
    public void StripTags_RemovesRichTextAndLineBreaks(string input, string expected)
    {
        Assert.That(BossCatalog.StripTags(input), Is.EqualTo(expected));
    }

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("   ", "")]
    [TestCase("lone wolf", "Lone wolf")]
    [TestCase("The Elder", "The Elder")]
    [TestCase("  the Elder ", "The Elder")]
    [TestCase("x", "X")]
    [TestCase("éa", "Éa")]
    public void CapitalizeFirst_ChangesOnlyTheFirstLetter(string input, string expected)
    {
        Assert.That(BossCatalog.CapitalizeFirst(input), Is.EqualTo(expected));
    }

    [TestCase("plain", "https://wsrv.nl/?url=plain&w=800&h=600&fit=contain&cbg=00000000&output=png")]
    [TestCase("https://x.test/a b.png", "https://wsrv.nl/?url=https%3A%2F%2Fx.test%2Fa%20b.png&w=800&h=600&fit=contain&cbg=00000000&output=png")]
    public void CenteredPhotoUrl_GoesThroughTheImageProxy(string url, string expected)
    {
        Assert.That(BossCatalog.CenteredPhotoUrl(url), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    public void CenteredPhotoUrl_EmptyStaysEmpty(string url)
    {
        Assert.That(BossCatalog.CenteredPhotoUrl(url), Is.EqualTo(url));
    }

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("plain", "plain")]
    [TestCase("a*b", "a\\*b")]
    [TestCase("a_b", "a\\_b")]
    [TestCase("a~b", "a\\~b")]
    [TestCase("a`b", "a\\`b")]
    [TestCase("a|b", "a\\|b")]
    [TestCase("a>b", "a\\>b")]
    [TestCase("a[b]c", "a\\[b\\]c")]
    [TestCase("a\\b", "a\\\\b")]
    [TestCase("# x", "\\# x")]
    [TestCase("## x", "\\## x")]
    [TestCase("-# x", "\\-# x")]
    [TestCase("- x", "\\- x")]
    [TestCase("  # x", "  \\# x")]
    [TestCase("\t- x", "\t\\- x")]
    [TestCase("a # b", "a # b")]
    [TestCase("a - b", "a - b")]
    [TestCase("a\n# b", "a\n\\# b")]
    [TestCase("a\r# b", "a\r\\# b")]
    [TestCase("a\r\n- b", "a\r\n\\- b")]
    [TestCase("1. x", "1\\. x")]
    [TestCase("12. x", "12\\. x")]
    [TestCase("1.5", "1\\.5")]
    [TestCase("1x", "1x")]
    [TestCase("12 x", "12 x")]
    [TestCase("5", "5")]
    [TestCase("a 1. b", "a 1. b")]
    [TestCase("x\n3. y", "x\n3\\. y")]
    [TestCase("1.", "1\\.")]
    [TestCase("1. *b*", "1\\. \\*b\\*")]
    public void EscapeDiscordMarkdown_EscapesWhatWouldRestyleAMessage(string input, string expected)
    {
        Assert.That(BossCatalog.EscapeDiscordMarkdown(input), Is.EqualTo(expected));
    }
}
