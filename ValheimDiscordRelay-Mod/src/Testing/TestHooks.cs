using System.Linq;
using BepInEx.Bootstrap;
using Kydl.Common;

namespace ValheimDiscordRelay.Testing
{
    /// <summary>
    /// KydlCore D-084: hooks for DrakesTestLab suites (ValheimDiscordRelay-Tests/TestLab/suites/ValheimDiscordRelay). A hook is a static method with
    /// [TestHook("name")]; DrakesTestLab finds it by the attribute's NAME (the mod does not reference DrakesTestLab). Hook names carry the mod id.
    /// </summary>
    internal static class TestHooks
    {
        /// <summary>All Startup_* / World_* checks of InGameTests: "PASS: ..." or "FAIL: ...".</summary>
        [TestHook("ValheimDiscordRelay.selftest")]
        static string SelfTest() { return TestLabSupport.RunInGameTests(typeof(InGameTests)); }

        /// <summary>How many entries the mod's one config file holds (the KY hub shows all of them on the Settings tab).</summary>
        [TestHook("ValheimDiscordRelay.configEntries")]
        static int ConfigEntries()
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(ValheimDiscordRelayPlugin.ModGuid, out info) || info.Instance == null) return -1;
            return info.Instance.Config.Keys.Count();
        }

        /// <summary>1 when [General] EnableAll is on (the default), 0 when off, -1 when the setting is not bound.</summary>
        [TestHook("ValheimDiscordRelay.enabled")]
        static int Enabled()
        {
            return ModSettings.EnableAll == null ? -1 : (ModSettings.EnableAll.Value ? 1 : 0);
        }
    }
}
