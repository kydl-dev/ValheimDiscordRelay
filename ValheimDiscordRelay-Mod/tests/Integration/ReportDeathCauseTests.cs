using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;

namespace ValheimDiscordRelay.Tests.Integration;

/// <summary>
/// Port of the legacy offline harness (T-011): the weekly report's death-cause tracking, run on the built mod DLL with fake deaths and no
/// game. Covers cause naming, the finishing hit, "highest amount by who", the same creature name as the Killer mobs tab, and that all of it
/// survives a save / load and the network encoding. It cannot test what needs the running game (Character.m_lastHit, the RPC, the window).
/// </summary>
[TestFixture]
public class ReportDeathCauseTests
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string Stats = "ValheimDiscordRelay.Report.ReportStats";
    const string Codec = "ValheimDiscordRelay.Report.ReportSnapshotCodec";

    string _dir;
    object _snapshotBeforeSave;
    object _snapshotAfterLoad;
    object _windowData;

    // ---- reflection helpers (the mod keeps its logic internal)
    static object Get(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
            if (p != null) return p.GetValue(o, null);
        }
        throw new MissingMemberException(o.GetType().FullName, name);
    }

    static IEnumerable<object> Items(object enumerable)
    {
        if (enumerable is IDictionary d)
        {
            foreach (object v in d.Values) yield return v;
            yield break;
        }
        foreach (object o in (IEnumerable)enumerable) yield return o;
    }

    /// <summary>A type of the game assembly (loaded from the ref folder).</summary>
    static Type GameType(string name)
    {
        Assembly game = Assembly.LoadFrom(Path.Combine(TestPaths.RefDir, "assembly_valheim.dll"));
        return game.GetType(name, true);
    }

    static string ResolveCause(string killer, string text, string env, string player)
    {
        return (string)ModAssembly.Call("ValheimDiscordRelay.Report.ReportTracker", "ResolveDeathCause", killer, text, env, player);
    }

    static void Death(string who, string prefab, string cause, double dmg, string type)
    {
        ModAssembly.Call(Stats, "RecordDeath", who);
        ModAssembly.Call(Stats, "RecordMobDeath", who, prefab, cause);
        ModAssembly.Call(Stats, "RecordDeathCause", who, cause, prefab);
        if (dmg > 0) ModAssembly.Call(Stats, "RecordDeathHit", who, cause, prefab, dmg, type);
    }

    [OneTimeSetUp]
    public void Run()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vdrtest" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        foreach (MethodInfo m in typeof(BepInEx.Paths).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.Name == "SetExecutablePath")
                m.Invoke(null, new object[] { Path.Combine(_dir, "valheim.exe"), Path.Combine(_dir, "BepInEx"), _dir, new string[0] });

        ConfigFile cf = new ConfigFile(Path.Combine(_dir, "t.cfg"), true);
        string report = "ValheimDiscordRelay.Report.ReportConfig";
        ModAssembly.SetStatic(report, "Enabled", cf.Bind("r", "e", true, ""));
        ModAssembly.SetStatic(report, "ClientEnabled", cf.Bind("r", "c", true, ""));
        ModAssembly.SetStatic(report, "WindowEnabled", cf.Bind("r", "w", true, ""));

        string statsPath = Path.Combine(_dir, "stats.txt");
        ModAssembly.SetStatic(Stats, "_path", statsPath);

        // the server records a death event, then the finishing-hit event (as ReportRpc.OnEvent does)
        Death("Astrid", "Troll", "Troll", 88, "Blunt");
        Death("Astrid", "Troll", "Troll", 120, "Blunt");
        Death("Astrid", "", "Fall damage", 15, "");
        Death("Bjorn", "Troll", "Troll", 70, "Blunt");
        Death("Bjorn", "Greydwarf_Elite", "$enemy_greydwarf_elite", 30, "Slash");  // a client whose game language is not English
        Death("Bjorn", "Greydwarf_Elite", "Greydwarf Brute", 31, "Slash");         // a client in English
        Death("Bjorn", "", "Drowning", 0, "");
        Death("Bjorn", "", "Lava", 40, "Fire");

        _snapshotBeforeSave = ModAssembly.Call(Stats, "Snapshot");

        // save, forget everything, load again
        ModAssembly.Call(Stats, "SaveIfDirty");
        object sync = ModAssembly.GetStatic(Stats, "Sync");
        lock (sync) ModAssembly.Call(Stats, "SaveLocked");
        ((IDictionary)ModAssembly.GetStatic(Stats, "Players")).Clear();
        Assert.That((bool)ModAssembly.Call(Stats, "Load"), Is.True, "stats file loads");
        _snapshotAfterLoad = ModAssembly.Call(Stats, "Snapshot");

        // snapshot -> wire -> window data
        object data = ModAssembly.Call(Codec, "FromSnapshot", _snapshotAfterLoad, "Test", DateTime.UtcNow.AddDays(1));
        Type zPackage = GameType("ZPackage");
        object pkg = Activator.CreateInstance(zPackage);
        ModAssembly.Call(Codec, "Write", pkg, data);
        zPackage.GetMethod("SetPos").Invoke(pkg, new object[] { 0 });
        _windowData = ModAssembly.Call(Codec, "Read", pkg);
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- 1. cause resolution (client side)
    [Test] public void CreatureKiller_Wins() { Assert.That(ResolveCause("Troll", "Astrid got killed", null, "Astrid"), Is.EqualTo("Troll")); }

    [Test]
    public void HitTypeCause_BeatsText_EvenWhenThePlayerIsCalledWaterman()
    {
        Assert.That(ResolveCause("", "Waterman got crushed by a falling tree.", "Falling tree", "Waterman"), Is.EqualTo("Falling tree"));
    }

    [Test]
    public void TextFallback_TreeIsNotFallDamage_AndPlayerNameIsIgnored()
    {
        Assert.That(ResolveCause("", "Waterman got crushed by a falling tree.", null, "Waterman"), Is.EqualTo("Falling tree"));
    }

    [Test]
    public void TextFallback_PlayerFirebeardDoesNotMakeItFire()
    {
        Assert.That(ResolveCause("", "Firebeard fell to death", null, "Firebeard"), Is.EqualTo("Fall damage"));
    }

    [Test] public void Lava_IsNotUnknown() { Assert.That(ResolveCause("", "Bjorn took a dip in lava", "Lava", "Bjorn"), Is.EqualTo("Lava")); }

    [Test]
    public void NothingKnown_IsUnknownEnvironment()
    {
        Assert.That(ResolveCause("", "Bjorn got killed by Unknown.", null, "Bjorn"), Is.EqualTo("Unknown / environment"));
    }

    [Test]
    public void EveryHitType_HasANameOrIsACreatureHit()
    {
        Type builder = ModAssembly.Type("ValheimDiscordRelay.Client.DeathMessageBuilder");
        MethodInfo m = builder.GetMethod("EnvironmentalCauseName", All);
        foreach (object t in Enum.GetValues(GameType("HitData+HitType")))
            Assert.DoesNotThrow(() => m.Invoke(null, new object[] { t }), t.ToString());
    }

    // ---- 2. server recording
    [Test]
    public void KillerMobsAndDeathCauses_UseTheSameCreatureName()
    {
        foreach (object p in Items(Get(_snapshotBeforeSave, "Players")))
        {
            IDictionary byMob = (IDictionary)Get(p, "DeathsByMob");
            IDictionary causes = (IDictionary)Get(p, "DeathCauses");
            string name = (string)Get(p, "Name");
            Assert.That(byMob.Contains("Greydwarf Brute"), Is.EqualTo(causes.Contains("Greydwarf Brute")), name);
            Assert.That(causes.Contains("Greydwarf Elite"), Is.False, name + ": no 'Greydwarf Elite' split");
        }
    }

    // ---- 3. save / load round trip
    [Test]
    public void StatsFile_HoldsTheFinishingHits()
    {
        string text = File.ReadAllText(Path.Combine(_dir, "stats.txt"));

        Assert.That(text, Does.Contain("deathhit\t"));
    }

    [Test]
    public void AfterSaveAndLoad_TheSnapshotKeepsTheDeathCauses()
    {
        Assert.That(Items(Get(_snapshotAfterLoad, "Players")), Is.Not.Empty);
    }

    // ---- 4. snapshot -> wire -> window data
    [Test] public void WindowData_DecodesWithDeathCauseDetails() { Assert.That(_windowData, Is.Not.Null); Assert.That((bool)Get(_windowData, "HasDeathCauseDetails"), Is.True); }

    [Test]
    public void ServerTroll_FinishingHit_Is120Blunt()
    {
        object hit = ((IDictionary)Get(_windowData, "DeathCauseHits"))["Troll"];

        Assert.That(Math.Round(Convert.ToDouble(Get(hit, "Damage"))), Is.EqualTo(120));
        Assert.That(Get(hit, "Type"), Is.EqualTo("Blunt"));
    }

    [Test]
    public void ServerTroll_HighestAmountBy_IsAstrid()
    {
        Assert.That(((IDictionary)Get(_windowData, "DeathCauseMostBy"))["Troll"], Is.EqualTo("Astrid"));
    }

    [Test]
    public void GreydwarfBrute_FinishingHit_Is31_BothClientsMerged()
    {
        object hit = ((IDictionary)Get(_windowData, "DeathCauseHits"))["Greydwarf Brute"];

        Assert.That(Math.Round(Convert.ToDouble(Get(hit, "Damage"))), Is.EqualTo(31));
    }

    [Test]
    public void Lava_HasItsOwnCause_With40Fire()
    {
        object hit = ((IDictionary)Get(_windowData, "DeathCauseHits"))["Lava"];

        Assert.That(Get(hit, "Type"), Is.EqualTo("Fire"));
        Assert.That(Math.Round(Convert.ToDouble(Get(hit, "Damage"))), Is.EqualTo(40));
    }
}
