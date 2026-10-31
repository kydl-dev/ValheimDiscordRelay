using System;
using FsCheck;

namespace ValheimDiscordRelay.Tests
{
    /// <summary>
    /// Property-based tests (Claude\testing\TEST_QUALITY.md). A property is a rule that must hold for EVERY input; FsCheck tries `runs`
    /// generated inputs and, on a failure, shrinks it to the smallest input that still breaks the rule and prints it with the seed to replay.
    /// Use:  Props.Check(Prop.ForAll(MyGen.ToArbitrary(), x => rule(x)));
    /// Write the rule from the documented behaviour (a round trip, a limit that always holds, "never throws"), never by copying the code's formula.
    /// </summary>
    internal static class Props
    {
        public const int DefaultRuns = 300;

        public static void Check(Property property, int runs = DefaultRuns)
        {
            var config = Configuration.QuickThrowOnFailure;
            config.MaxNbOfTest = runs;
            config.QuietOnSuccess = true;
            property.Check(config);
        }

        /// Floats as the game uses them for positions / angles: [-range, range] in 1 / 1000 steps.
        public static Gen<float> GameFloat(float range)
        {
            int r = (int)(range * 1000f);
            return Gen.Choose(-r, r).Select(i => i / 1000f);
        }

        /// Any finite float, including huge and tiny ones (bit patterns, so every exponent is reached).
        public static Gen<float> AnyFiniteFloat()
        {
            return Arb.Generate<int>().Select(bits => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0))
                                      .Where(f => !float.IsNaN(f) && !float.IsInfinity(f));
        }

        /// Text made of the given characters (separators of a save format, letters, symbols), 1..maxLength long.
        public static Gen<string> TextFrom(string chars, int maxLength)
        {
            return from n in Gen.Choose(1, maxLength)
                   from cs in Gen.ArrayOf(n, Gen.Elements(chars.ToCharArray()))
                   select new string(cs);
        }
    }
}
