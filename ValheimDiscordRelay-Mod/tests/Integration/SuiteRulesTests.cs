using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ValheimDiscordRelay.Tests.Integration
{
    /// <summary>
    /// Guards for the TestLab suite (lessons LESSONS_LEARNED 1.5, MIGRATION_DECISIONS C14): they catch suite rot at `dotnet test` time instead of
    /// after a 20-minute lab run. Every test JSON is listed in suite.json, every test checks noErrors for this mod, requireMods names this mod AND
    /// KydlCore with exact versions, configOverrides fix every player setting, every hook a JSON calls exists in the sources.
    /// </summary>
    [TestFixture]
    public class SuiteRulesTests
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string SuiteDir { get { return TestPaths.SuiteDir; } }
        static Dictionary<string, object> Suite() { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(SuiteDir, "suite.json"))); }
        static IEnumerable<string> TestFiles() { return Directory.GetFiles(SuiteDir, "*.json").Where(f => Path.GetFileName(f) != "suite.json"); }

        [Test] public void EveryTestFile_IsListed_AndEveryListedTestExists()
        {
            var listed = ((System.Collections.ArrayList)Suite()["tests"]).Cast<string>().ToList();
            var files = TestFiles().Select(Path.GetFileNameWithoutExtension).ToList();
            Assert.That(files.Except(listed), Is.Empty, "test files not in suite.json");
            Assert.That(listed.Except(files), Is.Empty, "suite.json lists missing tests");
        }

        [Test] public void EveryTest_ChecksNoErrorsOfThisMod()
        {
            foreach (var f in TestFiles())
                Assert.That(Regex.IsMatch(File.ReadAllText(f), @"""check""\s*:\s*""noErrors""\s*,\s*""source""\s*:\s*""ValheimDiscordRelay"""), Is.True, Path.GetFileName(f));
        }

        [Test] public void RequireMods_ThisModAndKydlCore_WithTheExactVersions()
        {
            var mods = ((System.Collections.ArrayList)Suite()["requireMods"]).Cast<Dictionary<string, object>>().ToList();
            var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(TestPaths.ModDir, "manifest.json")));
            var self = mods.SingleOrDefault(m => (string)m["guid"] == "com.kydldev.ValheimDiscordRelay");
            Assert.That(self, Is.Not.Null, "requireMods: com.kydldev.ValheimDiscordRelay");
            Assert.That(self["minVersion"], Is.EqualTo(manifest["version_number"]), "the suite tests exactly this version");
            var core = mods.SingleOrDefault(m => (string)m["guid"] == "com.kydldev.KydlCore");
            Assert.That(core, Is.Not.Null, "requireMods: com.kydldev.KydlCore (hard dependency)");
            var plugin = File.ReadAllText(Path.Combine(TestPaths.Src, "Bootstrap", "Plugin.cs"));
            var dep = Regex.Match(plugin, @"\[BepInDependency\(\s*KydlCore\.Bootstrap\.Plugin\.ModGuid\s*,\s*""(?<v>[^""]+)""");
            Assert.That(core["minVersion"], Is.EqualTo(dep.Groups["v"].Value), "KydlCore minVersion = the plugin's [BepInDependency] version");
        }

        [Test] public void ConfigOverrides_FixEveryPlayerSetting()
        {
            var src = Regex.Replace(File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs")), @"//.*$", "", RegexOptions.Multiline);   // examples in comments are not entries
            var bound = Regex.Matches(src, @"\.Bind\(\s*""(?<s>[^""]+)""\s*,\s*""(?<k>[^""]+)""").Cast<Match>().Select(m => m.Groups["s"].Value + "/" + m.Groups["k"].Value).ToList();
            var fixedOnes = ((System.Collections.ArrayList)Suite()["configOverrides"]).Cast<Dictionary<string, object>>()
                .Where(o => (string)o["guid"] == "com.kydldev.ValheimDiscordRelay").Select(o => (string)o["section"] + "/" + (string)o["key"]).ToList();
            Assert.That(bound.Except(fixedOnes), Is.Empty, "MIGRATION_DECISIONS C14: the suite sets every player setting (the user's manual config must not change a result)");
        }

        [Test] public void EveryTest_StartsInTheArena()
        {
            foreach (var f in TestFiles())
            {
                var test = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                var steps = test.ContainsKey("steps") ? ((System.Collections.ArrayList)test["steps"]).Cast<object>().ToList() : new List<object>();
                var first = steps.Count > 0 ? steps[0] as Dictionary<string, object> : null;
                Assert.That(first != null && Equals(first["do"], "hook") && Equals(first["name"], "valheimdiscordrelay.arena"), Is.True,
                    Path.GetFileName(f) + ": the first step must be the valheimdiscordrelay.arena hook (MIGRATION_DECISIONS C26: every test starts on the arena spot, away from the lab start)");
            }
        }

        [Test] public void NoTestLabScreenshots()
        {
            foreach (var f in TestFiles())
                Assert.That(Regex.IsMatch(File.ReadAllText(f), @"""do""\s*:\s*""screenshot"""), Is.False, Path.GetFileName(f) + ": no TestLab screen capture (user 2026-11-03) - use Kydl.GameProbe.ProbeApi.Screenshot");
        }

        [Test] public void EveryHookUsed_Exists()
        {
            var declared = new HashSet<string>(Directory.GetFiles(TestPaths.Src, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\[TestHook\(""(?<n>[^""]+)""\)\]").Cast<Match>().Select(m => m.Groups["n"].Value)));
            foreach (var f in TestFiles())
                foreach (Match m in Regex.Matches(File.ReadAllText(f), @"""name""\s*:\s*""(?<n>valheimdiscordrelay\.[^""]+)"""))
                    Assert.That(declared, Does.Contain(m.Groups["n"].Value), Path.GetFileName(f) + " calls a hook that does not exist");
        }
    }
}
