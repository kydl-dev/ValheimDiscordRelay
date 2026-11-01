using NUnit.Framework;

namespace ValheimDiscordRelay.Tests.Unit
{
    [TestFixture]
    public class TextLimitTests
    {
        [Test]
        public void ShortText_IsUnchanged()
        {
            Assert.That(TextLimit.WithEllipsis("abc", 5), Is.EqualTo("abc"));
        }

        [Test]
        public void TextExactlyAtTheLimit_IsUnchanged()
        {
            Assert.That(TextLimit.WithEllipsis("abcde", 5), Is.EqualTo("abcde"));
        }

        [Test]
        public void Null_StaysNull()
        {
            Assert.That(TextLimit.WithEllipsis(null, 5), Is.Null);
        }

        [Test]
        public void LongText_IsCutAndEndsWithEllipsis()
        {
            string cut = TextLimit.WithEllipsis("abcdefgh", 5);

            Assert.That(cut, Is.EqualTo("abcd\u2026"));
            Assert.That(cut.Length, Is.EqualTo(5));
        }

        [Test]
        public void Emoji_IsNotSplitInHalf()
        {
            // "ab" + U+1F600 (two chars) + "cd": cutting to 4 would keep "ab" + the first half of the emoji.
            string cut = TextLimit.WithEllipsis("ab\uD83D\uDE00cd", 4);

            Assert.That(cut, Is.EqualTo("ab\u2026"));
        }

        [Test]
        public void Emoji_ThatFitsCompletely_IsKept()
        {
            string cut = TextLimit.WithEllipsis("ab\uD83D\uDE00cd", 5);

            Assert.That(cut, Is.EqualTo("ab\uD83D\uDE00\u2026"));
        }
    }
}