using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using FsCheck;

namespace ValheimDiscordRelay.Tests.Unit
{
    /// <summary>
    /// The shared ModLog (Kydl.Common, compiled into the mod): line format, session marker, debug switch and the 2.5 s repeat filter
    /// (user standards). Reached by reflection in the built DLL; the BepInEx logger is real, only a capturing listener is added.
    /// </summary>
    [TestFixture]
    public class ModLogTests
    {
        const string LogType = "Kydl.Common.ModLog";
        static readonly Regex TimestampedLine = new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} - (?<msg>.*)$", RegexOptions.Singleline);

        sealed class CaptureListener : ILogListener
        {
            public readonly List<string> Lines = new List<string>();
            public void LogEvent(object sender, LogEventArgs eventArgs) { Lines.Add(Convert.ToString(eventArgs.Data)); }
            public void Dispose() { }
        }

        CaptureListener _listener;
        ManualLogSource _source;
        bool _debug;

        [SetUp]
        public void SetUp()
        {
            ModAssembly.SetStatic(LogType, "_source", null);
            ModAssembly.SetStatic(LogType, "_sessionStarted", false);
            ModAssembly.Call(LogType, "ResetRepeats");
            _debug = false;
            _listener = new CaptureListener();
            _source = new ManualLogSource("ValheimDiscordRelayUnitTest");
            BepInEx.Logging.Logger.Listeners.Add(_listener);
            BepInEx.Logging.Logger.Sources.Add(_source);
            ModAssembly.Call(LogType, "Init", _source, "ValheimDiscordRelay", (Func<bool>)(() => _debug));
        }

        [TearDown]
        public void TearDown()
        {
            BepInEx.Logging.Logger.Sources.Remove(_source);
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
            _source.Dispose();
            _listener.Dispose();
            ModAssembly.SetStatic(LogType, "_source", null);
            ModAssembly.SetStatic(LogType, "_sessionStarted", false);
        }

        static string Msg(string line) { return TimestampedLine.Match(line).Groups["msg"].Value; }

        [Test]
        public void Init_FirstCall_WritesTheSessionMarkerAsFirstLine()
        {
            Assert.That(_listener.Lines, Has.Count.EqualTo(1));
            Assert.That(Msg(_listener.Lines[0]), Is.EqualTo("NEW LOG SESSION STARTS HERE"));
        }

        [Test]
        public void Line_MessageWithBraces_IsNotTreatedAsAFormatString()
        {
            var line = (string)ModAssembly.Call(LogType, "Line", "100% {0} {broken");

            Assert.That(Msg(line), Is.EqualTo("100% {0} {broken"));
        }

        [Test]
        public void Info_SameMessageTwiceAtOnce_IsWrittenOnce()
        {
            ModAssembly.Call(LogType, "Info", "same");
            ModAssembly.Call(LogType, "Info", "same");

            Assert.That(_listener.Lines, Has.Count.EqualTo(2), "session marker + one 'same'");
        }

        [Test]
        public void Error_SameMessageTwice_IsAlwaysWritten()
        {
            ModAssembly.Call(LogType, "Error", "boom");
            ModAssembly.Call(LogType, "Error", "boom");

            Assert.That(_listener.Lines, Has.Count.EqualTo(3));
        }

        [Test]
        public void Verbose_DebugOff_WritesNothing()
        {
            ModAssembly.Call(LogType, "Verbose", "chatter");
            ModAssembly.Call(LogType, "VerboseWarn", "chatter");

            Assert.That(_listener.Lines, Has.Count.EqualTo(1), "only the session marker");
        }

        [Test]
        public void Verbose_DebugOn_IsWritten()
        {
            _debug = true;

            ModAssembly.Call(LogType, "Verbose", "chatter");

            Assert.That(_listener.Lines, Has.Count.EqualTo(2));
            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("chatter"));
        }

        [Test]
        public void Info_NullMessage_WritesPlaceholder()
        {
            Assert.DoesNotThrow(() => ModAssembly.Call(LogType, "Info", new object[] { null }));
            Assert.That(_listener.Lines[1], Does.EndWith(" - (null)"));
        }

        // ---- expected-value checks (user, 2026-10-24): off = WARNING always written, inside = debug line only

        [Test]
        public void Check_ValueInsideTolerance_ReturnsInsideAndWritesNothingWithDebugOff()
        {
            var inside = (bool)ModAssembly.Call(LogType, "Check", "bone turn", 24.0, 24.05, 2.0, "deg");

            Assert.That(inside, Is.True);
            Assert.That(_listener.Lines, Has.Count.EqualTo(1), "only the session line");
        }

        [Test]
        public void Check_ValueOutsideTolerance_WritesWarningWithExpectedValueEvenWithDebugOff()
        {
            var inside = (bool)ModAssembly.Call(LogType, "Check", "coil mismatch", 0.5, 0.0, 0.01, "m");

            Assert.That(inside, Is.False);
            Assert.That(_listener.Lines, Has.Count.EqualTo(2));
            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("CHECK OFF: coil mismatch = 0.5 m (expected 0 +- 0.01 m), off by 0.5 m"));
        }

        [Test]
        public void CheckRange_ValueAboveMax_WritesWarning()
        {
            var inside = (bool)ModAssembly.Call(LogType, "CheckRange", "scale", 1.5, 0.9, 1.1, "");

            Assert.That(inside, Is.False);
            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("CHECK OFF: scale = 1.5 (expected 0.9 .. 1.1)"));
        }

        [Test]
        public void Check_NaN_CountsAsOff()
        {
            Assert.That((bool)ModAssembly.Call(LogType, "Check", "x", double.NaN, 1.0, 1.0, "m"), Is.False);
        }

        // ---- added by the test-quality review (2026-10-26): each closes a gap Stryker showed (a code change no test noticed) ----------

        [Test]
        public void Init_ReturnsTrue_AndASecondInitDoesNotWriteASecondSessionMarker()
        {
            ModAssembly.Call(LogType, "ResetRepeats");   // without this the repeat filter would hide a second marker and the test proves nothing
            Assert.That((bool)ModAssembly.Call(LogType, "Init", _source, "ValheimDiscordRelay", (Func<bool>)(() => _debug)), Is.True);
            Assert.That(_listener.Lines, Has.Count.EqualTo(1), "still only the first session marker");
        }

        [TestCase("")]
        [TestCase(null)]
        public void Init_WithoutAModName_KeepsTheNameGivenBefore(string noName)
        {
            ModAssembly.Call(LogType, "Init", _source, "NameBefore", (Func<bool>)(() => _debug));
            ModAssembly.Call(LogType, "Init", _source, noName, (Func<bool>)(() => _debug));

            Assert.That(ModAssembly.GetStatic(LogType, "_modName"), Is.EqualTo("NameBefore"));
        }

        [Test]
        public void VerboseWarn_DebugOn_IsAWarning()
        {
            _debug = true;
            var levels = new List<LogLevel>();
            var listener = new LevelListener(levels);
            BepInEx.Logging.Logger.Listeners.Add(listener);
            try { ModAssembly.Call(LogType, "VerboseWarn", "careful"); }
            finally { BepInEx.Logging.Logger.Listeners.Remove(listener); }

            Assert.That(levels, Is.EqualTo(new[] { LogLevel.Warning }));
        }

        [Test]
        public void DebugSwitchThatThrowsNullReference_CountsAsOff()
        {
            ModAssembly.Call(LogType, "Init", _source, "ValheimDiscordRelay", (Func<bool>)(() => { throw new NullReferenceException("config not bound"); }));

            Assert.That((bool)ModAssembly.GetStaticProperty(LogType, "DebugOn"), Is.False);
            Assert.DoesNotThrow(() => ModAssembly.Call(LogType, "Verbose", "x"));
        }

        [Test]
        public void CheckRange_Inside_WithDebugOn_WritesCheckOk()
        {
            _debug = true;

            ModAssembly.Call(LogType, "CheckRange", "scale", 1.0, 0.9, 1.1, "x");

            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("CHECK ok: scale = 1 x (expected 0.9 .. 1.1 x)"));
        }

        [Test]
        public void Repeat_TableExactlyAtItsLimit_KeepsEveryLine()
        {
            for (int i = 0; i < 3998; i++) ModAssembly.Call(LogType, "Info", "line " + i);   // + the session marker = 3999
            Age(3.0);
            ModAssembly.Call(LogType, "Info", "fresh");                                        // 4000 = the limit, not over it

            Assert.That(Remembered().Count, Is.EqualTo(4000), "old lines are only dropped once the table holds MORE than 4000");
        }

        sealed class LevelListener : ILogListener
        {
            readonly List<LogLevel> _levels;
            public LevelListener(List<LogLevel> levels) { _levels = levels; }
            public void LogEvent(object sender, LogEventArgs e) { if (Convert.ToString(e.Data).EndsWith(" - careful")) _levels.Add(e.Level); }
            public void Dispose() { }
        }

        [Test]
        public void Info_LoggedBeforeInit_GoesToAFallbackSourceInsteadOfBeingLost()
        {
            ModAssembly.SetStatic(LogType, "_source", null);

            ModAssembly.Call(LogType, "Info", "early line");

            Assert.That(_listener.Lines, Has.Some.EndsWith(" - early line"));
        }

        [Test]
        public void Repeat_DifferentTextOrDifferentLevel_IsNotARepeat()
        {
            ModAssembly.Call(LogType, "Info", "a");
            ModAssembly.Call(LogType, "Info", "b");
            ModAssembly.Call(LogType, "Warn", "a");

            Assert.That(_listener.Lines, Has.Count.EqualTo(4), "marker + a + b + warning a: only level AND text together make a repeat");
        }

        [Test]
        public void Repeat_SameLineAfterTheWindow_IsWrittenAgain_InsideTheWindow_IsNot()
        {
            ModAssembly.Call(LogType, "Info", "tick");
            Age(2.4);
            ModAssembly.Call(LogType, "Info", "tick");
            Age(2.6);
            ModAssembly.Call(LogType, "Info", "tick");

            Assert.That(_listener.Lines, Has.Count.EqualTo(3), "marker + first tick + the tick 2.6 s later (the one 2.4 s later is a repeat)");
        }

        [Test]
        public void Repeat_TableOverItsLimit_ForgetsOnlyOldLines()
        {
            for (int i = 0; i <= 4000; i++) ModAssembly.Call(LogType, "Info", "line " + i);
            Age(3.0);
            ModAssembly.Call(LogType, "Info", "fresh");

            Assert.That(Remembered().Count, Is.EqualTo(1), "4002 remembered > 4000: every line older than the window is dropped, the fresh one stays");
            Assert.That((bool)ModAssembly.Call(LogType, "ResetRepeats"), Is.True);
            Assert.That(Remembered().Count, Is.Zero);
        }

        [Test]
        public void Check_ExactlyAtTheTolerance_IsInside_AndWithDebugOnWritesCheckOk()
        {
            _debug = true;

            var inside = (bool)ModAssembly.Call(LogType, "Check", "angle", 1.5, 1.0, 0.5, "deg");

            Assert.That(inside, Is.True);
            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("CHECK ok: angle = 1.5 deg (expected 1 +- 0.5 deg)"));
        }

        [Test]
        public void Check_NumbersAreWrittenWithThreeDecimals()
        {
            ModAssembly.Call(LogType, "Check", "third", 1.0 / 3.0, 0.0, 0.0, "");

            Assert.That(Msg(_listener.Lines[1]), Is.EqualTo("CHECK OFF: third = 0.333 (expected 0 +- 0), off by 0.333"));
        }

        [TestCase(0.9, true)]
        [TestCase(1.1, true)]
        [TestCase(1.0, true)]
        [TestCase(0.8999, false)]
        [TestCase(1.1001, false)]
        [TestCase(double.NaN, false)]
        public void CheckRange_BoundsAreInclusive(double actual, bool expected)
        {
            Assert.That((bool)ModAssembly.Call(LogType, "CheckRange", "scale", actual, 0.9, 1.1, ""), Is.EqualTo(expected));
            Assert.That(_listener.Lines, Has.Count.EqualTo(expected ? 1 : 2), "a warning only when off");
        }

        // ---- properties ------------------------------------------------------------------------------------------------------------

        [Test]
        public void Property_Check_InsideExactlyWhenWithinTolerance_AndOffIsAlwaysOneWarning()
        {
            var num = FsCheck.Gen.OneOf(FsCheck.Gen.Choose(-100000, 100000).Select(i => i / 100.0), FsCheck.Gen.Elements(double.NaN, double.PositiveInfinity, 0.0));
            var gen = from a in num from e in num from t in num select new[] { a, e, t };
            Props.Check(FsCheck.Prop.ForAll(gen.ToArbitrary(), x =>
            {
                SetUpFresh();
                var inside = (bool)ModAssembly.Call(LogType, "Check", "p", x[0], x[1], x[2], "");
                bool spec = !double.IsNaN(x[0]) && Math.Abs(x[0] - x[1]) <= Math.Abs(x[2]);
                bool oneWarningWhenOff = inside ? _listener.Lines.Count == 0 : _listener.Lines.Count == 1 && Msg(_listener.Lines[0]).StartsWith("CHECK OFF: p = ");
                return inside == spec && oneWarningWhenOff && inside == (bool)ModAssembly.Call(LogType, "Check", "q", x[0], x[1], -x[2], "");
            }));
        }

        [Test]
        public void Property_SameNonErrorLineTwice_IsWrittenOnce_ErrorsAlwaysTwice()
        {
            var text = FsCheck.Arb.Generate<string>();
            var level = FsCheck.Gen.Elements("Info", "Warn", "Error");
            Props.Check(FsCheck.Prop.ForAll(text.ToArbitrary(), level.ToArbitrary(), (message, method) =>
            {
                SetUpFresh();
                ModAssembly.Call(LogType, method, message);
                ModAssembly.Call(LogType, method, message);
                return _listener.Lines.Count == (method == "Error" ? 2 : 1);
            }));
        }

        /// Forget the lines of the previous property run and start counting from zero (session marker already written).
        void SetUpFresh()
        {
            ModAssembly.Call(LogType, "ResetRepeats");
            _debug = false;
            _listener.Lines.Clear();
        }

        static System.Collections.Generic.Dictionary<string, double> Remembered()
        {
            return (System.Collections.Generic.Dictionary<string, double>)ModAssembly.GetStatic(LogType, "LastWritten");
        }

        /// Pretends every remembered line was written `seconds` earlier (the repeat filter uses a Stopwatch, not the wall clock).
        static void Age(double seconds)
        {
            var table = Remembered();
            foreach (var key in new System.Collections.Generic.List<string>(table.Keys)) table[key] -= seconds;
        }
    }
}
