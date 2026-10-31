using System;
using System.Reflection;
using FsCheck;

namespace ValheimDiscordRelay.Tests.Unit
{
    /// <summary>
    /// Shared Kydl.Common.Tunable&lt;T&gt; (dev-only setting KydlTools pushes values into). Documented: with a range the value is always inside it
    /// (min / max swapped when given the wrong way round), SettingChanged fires only on a real change, Reset goes back to the fixed value.
    /// </summary>
    [TestFixture]
    public class TunableTests
    {
        sealed class Tun
        {
            readonly object _o;
            readonly Type _t;
            public int Changed;
            public Tun(object o)
            {
                _o = o; _t = o.GetType();
                var ev = _t.GetEvent("SettingChanged");
                ev.AddEventHandler(o, (EventHandler)((s, e) => Changed++));
            }
            public int Value { get { return (int)_t.GetProperty("Value").GetValue(_o); } set { _t.GetProperty("Value").SetValue(_o, value); } }
            public int Default { get { return (int)_t.GetProperty("Default").GetValue(_o); } }
            public bool Reset() { return (bool)_t.GetMethod("Reset").Invoke(_o, null); }
        }

        static Type IntTunable { get { return ModAssembly.Type("Kydl.Common.Tunable`1").MakeGenericType(typeof(int)); } }
        static Tun Ranged(int value, int min, int max) { return new Tun(Activator.CreateInstance(IntTunable, "S", "N", value, min, max, "")); }
        static Tun Free(int value) { return new Tun(Activator.CreateInstance(IntTunable, "S", "N", value, "")); }

        static readonly Gen<int> Small = Gen.Choose(-1000, 1000);

        [Test]
        public void Ranged_ValueOutsideAtCreation_IsClampedAndBecomesTheFixedValue()
        {
            var t = Ranged(50, 0, 10);

            Assert.That(t.Value, Is.EqualTo(10));
            Assert.That(t.Default, Is.EqualTo(10));
        }

        [Test]
        public void Ranged_MinAndMaxSwapped_StillClamps()
        {
            var t = Ranged(5, 10, 0);
            t.Value = -3;

            Assert.That(t.Value, Is.EqualTo(0));
        }

        [Test]
        public void SettingChanged_FiresOnlyOnARealChange()
        {
            var t = Ranged(5, 0, 10);
            t.Value = 5;
            t.Value = 99;    // clamped to 10: a change
            t.Value = 50;    // clamped to 10 again: no change

            Assert.That(t.Changed, Is.EqualTo(1));
        }

        [Test]
        public void SetFromConfigOf_MarksExternal_AndReturnsTheSameTunable()
        {
            var o = Activator.CreateInstance(IntTunable, "Sec", "Name", 3, "");
            Assert.That(IntTunable.GetProperty("External").GetValue(o), Is.False);

            var back = IntTunable.GetMethod("SetFromConfigOf").Invoke(o, new object[] { "OtherMod" });

            Assert.That(back, Is.SameAs(o));
            Assert.That(IntTunable.GetProperty("External").GetValue(o), Is.True);
            Assert.That(o.ToString(), Is.EqualTo("Sec.Name = 3"));
        }

        [Test]
        public void Description_NullBecomesEmpty_OtherwiseKept()
        {
            Assert.That(IntTunable.GetProperty("Description").GetValue(Activator.CreateInstance(IntTunable, "S", "N", 1, null)), Is.EqualTo(""));
            Assert.That(IntTunable.GetProperty("Description").GetValue(Activator.CreateInstance(IntTunable, "S", "N", 1, "what it does")), Is.EqualTo("what it does"));
        }

        [Test]
        public void Property_RangedValue_IsAlwaysInsideTheRange()
        {
            var gen = from v in Small from a in Small from b in Small from set in Small select new[] { v, a, b, set };
            Props.Check(Prop.ForAll(gen.ToArbitrary(), x =>
            {
                int lo = Math.Min(x[1], x[2]), hi = Math.Max(x[1], x[2]);
                var t = Ranged(x[0], x[1], x[2]);
                bool createdInside = t.Value >= lo && t.Value <= hi;
                t.Value = x[3];
                return createdInside && t.Value == Math.Max(lo, Math.Min(hi, x[3]));
            }));
        }

        [Test]
        public void Property_Reset_GoesBackToTheFixedValue_AndSaysSo()
        {
            var gen = from v in Small from a in Small from b in Small from set in Small select new[] { v, a, b, set };
            Props.Check(Prop.ForAll(gen.ToArbitrary(), x =>
            {
                var t = Ranged(x[0], x[1], x[2]);
                int fixedValue = t.Default;
                t.Value = x[3];
                return t.Reset() && t.Value == fixedValue;
            }));
        }

        [Test]
        public void Property_NoRange_KeepsAnyValue_AndFiresOncePerChange()
        {
            Props.Check(Prop.ForAll(Arb.From(Small), Arb.From(Small), (start, set) =>
            {
                var t = Free(start);
                t.Value = set;
                t.Value = set;
                return t.Value == set && t.Changed == (set == start ? 0 : 1);
            }));
        }
    }
}
