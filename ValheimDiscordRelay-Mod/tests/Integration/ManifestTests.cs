using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ValheimDiscordRelay.Tests.Integration
{
    /// <summary>
    /// manifest.json, dependencies, version consistency and Hexium's package rules (https://hexium.gg/packaging; the user publishes on Hexium
    /// only). Plugin.ModVersion, manifest.json and the newest CHANGELOG heading must always agree (a mismatch breaks version checks between players).
    /// </summary>
    [TestFixture]
    public class ManifestTests
    {
        static readonly Regex DependencyFormat = new Regex(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+$");
        static readonly Regex SemVer = new Regex(@"^\d+\.\d+\.\d+(-(alpha|beta|rc)\.\d+)?$");

        static string ModFile(string name) { return Path.Combine(TestPaths.ModDir, name); }

        static Dictionary<string, object> Manifest()
        {
            return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ModFile("manifest.json")));
        }

        static string Str(string key) { return Manifest()[key] as string; }

        static string PluginConst(string name)
        {
            var m = Regex.Match(File.ReadAllText(Path.Combine(TestPaths.Src, "Bootstrap", "Plugin.cs")), "const string " + name + @"\s*=\s*""([^""]*)""");
            Assert.That(m.Success, Is.True, "const " + name + " not found in src/Plugin.cs");
            return m.Groups[1].Value;
        }

        static List<string> Dependencies() { return ((IEnumerable)Manifest()["dependencies"]).Cast<object>().Select(o => (string)o).ToList(); }

        [Test]
        public void Manifest_File_HasAllRequiredFields()
        {
            var m = Manifest();
            foreach (var key in new[] { "name", "version_number", "website_url", "description", "dependencies" })
                Assert.That(m.ContainsKey(key), Is.True, "missing field " + key);
        }

        [Test]
        public void Manifest_Name_MatchesThePluginNameAndHexiumCharacterRules()
        {
            Assert.That(Str("name"), Is.EqualTo(PluginConst("ModName")));
            Assert.That(Str("name"), Does.Match(@"^[A-Za-z0-9_]{1,128}$"), "Hexium: 1 - 128 letters, digits and underscores");
        }

        [Test]
        public void Manifest_Version_IsSemVerAndEqualsPluginModVersion()
        {
            Assert.That(Str("version_number"), Does.Match(SemVer.ToString()));
            Assert.That(Str("version_number"), Is.EqualTo(PluginConst("ModVersion")), "manifest.json and Plugin.ModVersion must agree");
        }

        [Test]
        public void Manifest_Version_EqualsTheNewestChangelogHeading()
        {
            var heading = File.ReadLines(ModFile("CHANGELOG.md")).FirstOrDefault(l => Regex.IsMatch(l, @"^## \d+\.\d+\.\d+"));

            Assert.That(heading, Is.Not.Null, "no '## x.y.z' heading in CHANGELOG.md");
            Assert.That(Regex.Match(heading, @"\d+\.\d+\.\d+").Value, Is.EqualTo(Str("version_number")));
        }

        [Test]
        public void Manifest_Dependencies_UseFormatAndContainBepInExAndJotunn()
        {
            var deps = Dependencies();
            foreach (var d in deps) Assert.That(DependencyFormat.IsMatch(d), Is.True, "bad dependency string: " + d);
            Assert.That(deps.Any(d => d.IndexOf("BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase) >= 0), Is.True);
            Assert.That(deps.Any(d => d.IndexOf("Jotunn", StringComparison.OrdinalIgnoreCase) >= 0), Is.True);
        }

        /// The KydlCore version asked for by the plugin attribute: [BepInDependency(KydlCore.Bootstrap.Plugin.ModGuid, "x.y.z")] (or the GUID text).
        static string PluginCoreDependencyVersion()
        {
            var m = Regex.Match(File.ReadAllText(Path.Combine(TestPaths.Src, "Bootstrap", "Plugin.cs")), @"\[BepInDependency\(\s*(KydlCore\.Bootstrap\.Plugin\.ModGuid|""com\.kydldev\.KydlCore"")\s*,\s*""(\d+\.\d+\.\d+)""");
            return m.Success ? m.Groups[2].Value : null;
        }

        [Test]
        public void KydlCore_IsAHardDependency_InPluginAndManifest_WithTheSameVersion()
        {
            var attribute = PluginCoreDependencyVersion();
            Assert.That(attribute, Is.Not.Null, "Plugin.cs needs [BepInDependency(KydlCore.Bootstrap.Plugin.ModGuid, \"x.y.z\")] (hard dependency with a minimum version, every KydlDev mod requires KydlCore)");
            var dep = Dependencies().FirstOrDefault(d => d.StartsWith("KydlDev-KydlCore-", StringComparison.Ordinal));
            Assert.That(dep, Is.Not.Null, "manifest.json dependencies need KydlDev-KydlCore-x.y.z");
            Assert.That(dep.Substring("KydlDev-KydlCore-".Length), Is.EqualTo(attribute), "the KydlCore version in manifest.json and in the [BepInDependency] attribute must agree");
        }

        [Test]
        public void KydlCore_Dependency_IsNotNewerThanTheKydlCoreTheModIsBuiltAgainst()
        {
            // the KydlCore project next to this one (E:\Projects\Valheim\KydlCore): its manifest.json version is the newest KydlCore there is (KydlCore.dll carries no file version)
            var manifest = Environment.GetEnvironmentVariable("KYDL_CORE_MANIFEST") ?? Path.GetFullPath(Path.Combine(TestPaths.ProjectRoot, "..", "KydlCore", "KydlCore-Mod", "manifest.json"));
            Assume.That(File.Exists(manifest), "KydlCore manifest.json not found (set KYDL_CORE_MANIFEST)");
            var core = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(manifest));
            var newest = Version.Parse((string)core["version_number"]);
            var declared = Version.Parse(PluginCoreDependencyVersion());

            Assert.That(declared, Is.LessThanOrEqualTo(newest), "the mod asks for a newer KydlCore than the newest one (" + newest + ")");
        }

        [Test]
        public void Manifest_JotunnDependency_IsNotNewerThanTheJotunnTheModIsBuiltAgainst()
        {
            var dep = Dependencies().First(d => d.IndexOf("Jotunn", StringComparison.OrdinalIgnoreCase) >= 0);
            var declared = Version.Parse(dep.Substring(dep.LastIndexOf('-') + 1));
            var dll = Path.Combine(TestPaths.RefDir, "Jotunn.dll");
            Assume.That(File.Exists(dll), "Jotunn.dll not in the reference folder");
            var built = Version.Parse(FileVersionInfo.GetVersionInfo(dll).FileVersion);

            Assert.That(new Version(declared.Major, declared.Minor, declared.Build), Is.LessThanOrEqualTo(new Version(built.Major, built.Minor, built.Build)),
                "manifest asks for a newer Jotunn than the one the mod is compiled against");
        }

        [Test]
        public void Package_RequiredFiles_Exist()
        {
            foreach (var f in new[] { "manifest.json", "README.md", "icon.png", "CHANGELOG.md" })
                Assert.That(File.Exists(ModFile(f)), Is.True, f);
        }

        // ---- Hexium package rules: release gates ----

        [Category("ReleaseGate")]
        [Test]
        public void Manifest_Description_IsAtMost256CharactersAndNotTbd()
        {
            Assert.That(Str("description").Length, Is.LessThanOrEqualTo(256), "Hexium: the description is at most 256 characters");
            Assert.That(Str("description"), Does.Not.StartWith("TBD"), "write the real description before publishing");
        }

        [Category("ReleaseGate")]
        [Test]
        public void Icon_File_Is256By256Png()
        {
            var b = File.ReadAllBytes(ModFile("icon.png"));
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            Assert.That(b.Take(8).ToArray(), Is.EqualTo(png), "not a PNG file");
            int width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            int height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            Assert.That(new[] { width, height }, Is.EqualTo(new[] { 256, 256 }), "Hexium requires exactly 256x256");
        }
    }
}
