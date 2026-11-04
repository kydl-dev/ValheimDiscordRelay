using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay.Tests.Integration;

/// <summary>
/// User rule: the player config holds only what a player should change, everything else is a Tunable (dev-only, shown by KydlTools).
/// KydlTools finds Tunables by reflection (type name "Tunable`1", properties Section / Name / Description / Default / Value / External):
/// these tests keep that contract from breaking.
/// </summary>
[TestFixture]
public class SettingsTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static List<object> Tunables()
    {
        var list = new List<object>();
        foreach (var f in ModAssembly.Type("ValheimDiscordRelay.ModSettings").GetFields(Static))
            if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name == "Tunable`1") list.Add(f.GetValue(null));
        return list;
    }

    [Test]
    public void Tunables_HaveTheMembersKydlToolsReads()
    {
        foreach (var t in Tunables())
            foreach (var p in new[] { "Section", "Name", "Description", "Default", "Value", "External" })
                Assert.That(t.GetType().GetProperty(p), Is.Not.Null, "Tunable property " + p + " missing (KydlTools reads it)");
    }

    [Test]
    public void Tunables_StartAtTheirFixedValue()
    {
        foreach (var t in Tunables())
        {
            var ty = t.GetType();
            Assert.That(ty.GetProperty("Value").GetValue(t, null), Is.EqualTo(ty.GetProperty("Default").GetValue(t, null)), t.ToString());
        }
    }

    [Test]
    public void PlayerConfig_HasDebugLogSwitch()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));

        Assert.That(Regex.IsMatch(src, @"Bind\(\s*""DebugLog""\s*,\s*""Enable""\s*,\s*false"), Is.True, "[DebugLog] Enable (off by default) is required in every mod");
    }

    [Test]
    public void PlayerConfig_HasEnableAll()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));

        Assert.That(Regex.IsMatch(src, @"Bind\(\s*""General""\s*,\s*""EnableAll""\s*,\s*true"), Is.True, "[General] EnableAll (on by default) is the global switch of every mod");
    }

    [Test]
    public void PlayerConfig_EverySectionHasEnable()
    {
        // PROJECT_RULES 11 (user, 2026-10-30): every functionality has its own [Section] Enable; General holds EnableAll, DebugLog holds its Enable
        var src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));
        var sections = new HashSet<string>();
        var withEnable = new HashSet<string>();
        foreach (Match m in Regex.Matches(src, @"\.Bind\(\s*""(?<s>[^""]+)""\s*,\s*""(?<k>[^""]+)"""))
        {
            sections.Add(m.Groups["s"].Value);
            if (m.Groups["k"].Value == "Enable") withEnable.Add(m.Groups["s"].Value);
        }
        sections.Remove("General");

        foreach (var s in sections)
            Assert.That(withEnable.Contains(s), Is.True, "config section [" + s + "] has no 'Enable' entry: every functionality gets its own switch (PROJECT_RULES 11); a setting that is not a functionality belongs in [General]");
    }
    [Test]
    public void PlayerConfig_HasNoMenuKey()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Regex.IsMatch(src, @"Bind\(\s*""[^""]*""\s*,\s*""MenuKey"""), Is.False, "no mod has its own menu key: the menu is tabs in the KY hub (F6, KydlCore)");
            Assert.That(Regex.IsMatch(src, @"ConfigEntry<KeyCode>\s+MenuKey"), Is.False, "no MenuKey entry (KydlCore owns the hub key)");
        }
    }

    [Test]
    public void ConfigDescriptions_DoNotTalkAboutOldVersions()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));

        foreach (Match m in Regex.Matches(src, @"Bind\([^;]*?""(?<d>[^""]{20,})"""))
            Assert.That(Regex.IsMatch(m.Groups["d"].Value, @"\b(previous|formerly|renamed|old value|used to|before version)\b", RegexOptions.IgnoreCase), Is.False, m.Groups["d"].Value);
    }
}
