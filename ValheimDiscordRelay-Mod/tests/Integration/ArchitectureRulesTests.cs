using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay.Tests.Integration
{
    /// <summary>
    /// The KydlDev spec / Architecture V2 rules, checked on the sources (MIGRATION_DECISIONS A3-A9, B10): pure layers reference no game or
    /// engine, Harmony patches live only in src/Integrations/Harmony, KydlCore stays a hard dependency, exact network versions, the
    /// architecture marker the lab wrapper looks for. Keep these green; a deliberate deviation is written in docs/Architecture.md AND here.
    /// </summary>
    [TestFixture]
    public class ArchitectureRulesTests
    {
        static readonly Regex ForbiddenUsing = new Regex(@"^\s*using\s+(UnityEngine|HarmonyLib|BepInEx|Jotunn|KydlCore(?!\.Core\b))\b", RegexOptions.Multiline);
        static readonly Regex ForbiddenType = new Regex(@"\b(GameObject|Component|MonoBehaviour|Transform|Vector3|Quaternion|Character|Humanoid|MonsterAI|BaseAI|ZNetView|ZDO|ZNet|ZNetScene|Player|Time\.time|Physics|ModLog|KyHub)\b");
        static readonly Regex LineComment = new Regex(@"//.*$", RegexOptions.Multiline);
        static readonly Regex StringLiteral = new Regex(@"""([^""\\]|\\.)*""");

        static string Src { get { return TestPaths.Src; } }
        static string SharedCore { get { return Path.GetFullPath(Path.Combine(TestPaths.ProjectRoot, "..", "KydlCore", "KydlCore-Mod", "src", "Shared", "Core")); } }

        static IEnumerable<string> PureFiles()
        {
            var files = new List<string>();
            var core = Path.Combine(Src, "Core");
            if (Directory.Exists(core)) files.AddRange(Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories));
            foreach (var dir in Directory.GetDirectories(Path.Combine(Src, "Features"), "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dir);
                if (name == "Domain" || name == "Application" || name == "Execution") files.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
            }
            return files.Distinct();
        }

        static List<string> Violations(IEnumerable<string> files)
        {
            var bad = new List<string>();
            foreach (var f in files)
            {
                string code = StringLiteral.Replace(LineComment.Replace(File.ReadAllText(f), ""), "\"\"");
                var u = ForbiddenUsing.Match(code);
                if (u.Success) bad.Add(Path.GetFileName(f) + ": " + u.Value.Trim());
                var t = ForbiddenType.Match(code);
                if (t.Success) bad.Add(Path.GetFileName(f) + ": names " + t.Value);
            }
            return bad;
        }

        [Test] public void PureLayers_ReferenceNoGameOrEngine()
        {
            Assert.That(PureFiles(), Is.Not.Empty, "src/Features/<F>/{Domain,Application,Execution}");
            Assert.That(Violations(PureFiles()), Is.Empty, "the domain decides without the game - move game reads into the feature's Infrastructure adapter");
        }

        [Test] public void KydlCoreContracts_ArePure()
        {
            Assert.That(Directory.Exists(SharedCore), Is.True, SharedCore);
            Assert.That(Violations(Directory.GetFiles(SharedCore, "*.cs", SearchOption.AllDirectories)), Is.Empty, "KydlCore\\KydlCore-Mod\\src\\Shared\\Core must stay pure");
        }

        [Test] public void HarmonyPatches_LiveOnlyIn_IntegrationsHarmony()
        {
            var harmonyDir = Path.Combine(Src, "Integrations", "Harmony") + Path.DirectorySeparatorChar;
            var outside = Directory.GetFiles(Src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(harmonyDir) && Regex.IsMatch(LineComment.Replace(File.ReadAllText(f), ""), @"\[HarmonyPatch\b|\.Patch\("))
                .Select(Path.GetFileName).ToList();
            Assert.That(outside, Is.Empty, "MIGRATION_DECISIONS A5: Harmony patches go into src/Integrations/Harmony");
        }

        [Test] public void Plugin_HardKydlCore_ExactNetworkVersion()
        {
            string plugin = File.ReadAllText(Path.Combine(Src, "Bootstrap", "Plugin.cs"));
            string proj = File.ReadAllText(Directory.GetFiles(TestPaths.ModDir, "*.csproj").Single());
            Assert.That(proj, Does.Contain("<KydlUseCore>true</KydlUseCore>"));
            Assert.That(plugin, Does.Contain("[BepInDependency(KydlCore.Bootstrap.Plugin.ModGuid"));
            Assert.That(Regex.IsMatch(plugin, @"\[NetworkCompatibility\(CompatibilityLevel\.(EveryoneMustHaveMod|VersionCheckOnly), VersionStrictness\.Patch\)\]"), Is.True,
                "MIGRATION_DECISIONS: exact versions (Patch); EveryoneMustHaveMod for shared-world mods, VersionCheckOnly for client-only mods");
        }

        [Test] public void EveryFeature_HasAnAdapterImplementingIFeature()
        {
            foreach (var feature in Directory.GetDirectories(Path.Combine(Src, "Features")))
            {
                var infra = Path.Combine(feature, "Infrastructure");
                bool has = Directory.Exists(infra) && Directory.GetFiles(infra, "*.cs", SearchOption.AllDirectories).Any(f => Regex.IsMatch(File.ReadAllText(f), @":\s*IFeature\b"));
                Assert.That(has, Is.True, Path.GetFileName(feature) + ": Infrastructure/<X>Feature.cs implementing IFeature (MIGRATION_DECISIONS A4)");
            }
        }

        [Test] public void ArchitectureMarker_IsPresent()
        {
            Assert.That(File.ReadAllText(Path.Combine(Src, "Core", "Architecture.cs")), Does.Contain("Tag = \"ValheimDiscordRelay-KYDL-SPEC-V1\""));
        }
    }
}
