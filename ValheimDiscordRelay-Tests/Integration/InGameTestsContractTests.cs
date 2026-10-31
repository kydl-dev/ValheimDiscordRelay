using System;
using System.Linq;
using System.Reflection;

namespace ValheimDiscordRelay.Tests.Integration
{
    /// <summary>
    /// The mod's in-game tests (src/Testing/InGameTests.cs) are found by Kydl.GameProbe by NAME and signature only, so a typo would make a
    /// test silently never run. These tests check what GameProbe needs: a class called InGameTests, methods Startup_* / World_* that are
    /// static, have no parameters and return string. (Running them needs the game: GameProbe does that in every session.)
    /// </summary>
    [TestFixture]
    public class InGameTestsContractTests
    {
        static MethodInfo[] Candidates()
        {
            var types = LoadableTypes().Where(t => t.Name == "InGameTests").ToArray();
            return types.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        .Where(m => m.Name.StartsWith("Startup_", StringComparison.Ordinal) || m.Name.StartsWith("World_", StringComparison.Ordinal)).ToArray();
        }

        // A mod that references another mod (WaterAndTarPools -> DrainTarPit) has types the test runner cannot load: skip those, keep the rest.
        static Type[] LoadableTypes()
        {
            try { return ModAssembly.Load().GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        [Test]
        public void InGameTests_ClassExistsWithAtLeastOneTest()
        {
            Assert.That(Candidates(), Is.Not.Empty, "no InGameTests.Startup_* / World_* method in the mod DLL (template: src/Testing/InGameTests.cs)");
        }

        [Test]
        public void InGameTests_EveryTestHasTheSignatureGameProbeRuns()
        {
            var bad = Candidates().Where(m => !m.IsStatic || m.GetParameters().Length != 0 || m.ReturnType != typeof(string))
                                  .Select(m => m.DeclaringType.Name + "." + m.Name).ToList();
            Assert.That(bad, Is.Empty, "GameProbe only runs static, parameterless methods returning string: " + string.Join(", ", bad));
        }
    }
}
