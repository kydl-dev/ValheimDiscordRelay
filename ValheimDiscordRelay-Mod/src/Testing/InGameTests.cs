using System.Linq;
using BepInEx.Bootstrap;

namespace ValheimDiscordRelay.Testing
{
    /// <summary>
    /// In-game tests run by the user's Kydl.GameProbe (dev tool, never a dependency: it finds this class by its NAME `InGameTests`).
    ///   Startup_*  run when the main menu is reached;   World_*  run 5 s after a world is loaded; both again on GameProbe's RetestKey (PageDown).
    /// Each method: static, no parameters, returns a text starting with PASS / FAIL / SKIP and the detail with numbers + units.
    /// Keep them cheap and read-only.
    /// </summary>
    internal static class InGameTests
    {
        /// The one plugin of the mod is loaded by BepInEx (in the game and on a dedicated server alike).
        static string Startup_PluginLoaded()
        {
            return Chainloader.PluginInfos.ContainsKey(ValheimDiscordRelayPlugin.ModGuid)
                ? "PASS: loaded " + ValheimDiscordRelayPlugin.ModGuid + " " + ValheimDiscordRelayPlugin.ModVersion
                : "FAIL: " + ValheimDiscordRelayPlugin.ModGuid + " is not loaded";
        }

        /// The mod has ONE config file and it is bound (the KY hub shows its entries on the Settings tab).
        static string Startup_OneConfigFileWithEntries()
        {
            PluginInfoLookup info = new PluginInfoLookup();
            if (info.Plugin == null) return "FAIL: plugin not found";
            var keys = info.Plugin.Config.Keys.ToList();
            bool file = info.Plugin.Config.ConfigFilePath.EndsWith(ValheimDiscordRelayPlugin.ModGuid + ".cfg");
            return file && keys.Count >= 60 && ModSettings.EnableAll != null
                ? "PASS: " + keys.Count + " entries in " + System.IO.Path.GetFileName(info.Plugin.Config.ConfigFilePath)
                : "FAIL: " + keys.Count + " entries (want >= 60) in " + info.Plugin.Config.ConfigFilePath;
        }

        /// The config rule (PROJECT_RULES 11): [General] EnableAll and [DebugLog] Enable exist, the mod is on.
        static string Startup_ConfigRules()
        {
            if (ModSettings.EnableAll == null || ModSettings.DebugLog == null) return "FAIL: ModSettings.Bind did not run (EnableAll / DebugLog null)";
            return ModSettings.EnableAll.Value ? "PASS: EnableAll on, debug log " + ModSettings.DebugLog.Value : "SKIP: EnableAll is off in the config";
        }

        /// KydlCore (hard dependency) is loaded: it owns the KY hub (F6) with this mod's Settings tab.
        static string Startup_CoreLoaded()
        {
            bool loaded = Chainloader.PluginInfos.ContainsKey(KydlCore.Bootstrap.Plugin.ModGuid);
            return loaded ? "PASS: " + KydlCore.Bootstrap.Plugin.ModGuid + " is loaded" : "FAIL: " + KydlCore.Bootstrap.Plugin.ModGuid + " is not loaded (this mod requires it)";
        }

        /// Every "Server - " entry is either synced to the players (ServerSync tag) or admin-only (never sent to players); no "Client - " entry is admin-only.
        static string Startup_ServerEntriesProtected()
        {
            PluginInfoLookup info = new PluginInfoLookup();
            if (info.Plugin == null) return "FAIL: plugin not found";
            int server = 0, adminOnly = 0, synced = 0;
            var bad = new System.Text.StringBuilder();
            foreach (var def in info.Plugin.Config.Keys)
            {
                var entry = info.Plugin.Config[def];
                bool isAdminOnly = entry.Description.Tags.Any(t => t is Server.AdminOnlyTag);
                // ServerSync's own tag has a "ReadOnly" member (the one the KY hub reads to lock the entry for non-admins)
                bool isSynced = entry.Description.Tags.Any(t => t != null && (t.GetType().GetField("ReadOnly") != null || t.GetType().GetProperty("ReadOnly") != null));
                if (def.Key.StartsWith("Server - "))
                {
                    server++;
                    if (isAdminOnly) adminOnly++; else if (isSynced) synced++; else bad.Append(def.Section).Append('/').Append(def.Key).Append(" (neither synced nor admin-only); ");
                }
                else if (def.Key.StartsWith("Client - ") && isAdminOnly)
                    bad.Append(def.Section).Append('/').Append(def.Key).Append(" (a client entry is admin-only); ");
            }
            if (server == 0) return "FAIL: no \"Server - \" entries found";
            return bad.Length == 0 ? "PASS: " + server + " server entries (" + synced + " synced, " + adminOnly + " admin-only)" : "FAIL: " + bad;
        }

        /// The local player exists once a world is loaded.
        static string World_PlayerPresent()
        {
            var p = Player.m_localPlayer;
            return p != null ? "PASS: " + p.GetPlayerName() + " at " + p.transform.position.ToString("F1") : "FAIL: no local player 5 s after the world loaded";
        }

        sealed class PluginInfoLookup
        {
            public BepInEx.BaseUnityPlugin Plugin
            {
                get
                {
                    BepInEx.PluginInfo i;
                    return Chainloader.PluginInfos.TryGetValue(ValheimDiscordRelayPlugin.ModGuid, out i) ? i.Instance : null;
                }
            }
        }
    }
}
