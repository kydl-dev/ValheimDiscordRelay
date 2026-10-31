using BepInEx.Bootstrap;

namespace ValheimDiscordRelay.Testing
{
    /// <summary>
    /// In-game tests run by the user's Kydl.GameProbe (dev tool, never a dependency: it finds this class by its NAME `InGameTests`).
    ///   Startup_*  run when the main menu is reached;   World_*  run 5 s after a world is loaded; both again on GameProbe's RetestKey (PageDown).
    /// Each method: static, no parameters, returns a text starting with PASS / FAIL / SKIP and the detail with numbers + units.
    /// Keep them cheap and read-only. This mod is a client plugin AND a server plugin in one DLL: only the one that fits the game runs.
    /// </summary>
    internal static class InGameTests
    {
        /// One of the two plugins of this DLL is loaded by BepInEx (client in the game, server on a dedicated server).
        static string Startup_PluginLoaded()
        {
            var client = Chainloader.PluginInfos.ContainsKey(Client.ClientPlugin.PluginGuid);
            var server = Chainloader.PluginInfos.ContainsKey(Server.ServerPlugin.PluginGuid);
            if (client || server) return "PASS: loaded " + (client ? "client " : "") + (server ? "server " : "") + "plugin, version " + Client.ClientPlugin.PluginVersion;
            return "FAIL: neither " + Client.ClientPlugin.PluginGuid + " nor " + Server.ServerPlugin.PluginGuid + " is loaded";
        }

        /// The local player exists once a world is loaded.
        static string World_PlayerPresent()
        {
            var p = Player.m_localPlayer;
            return p != null ? "PASS: " + p.GetPlayerName() + " at " + p.transform.position.ToString("F1") : "FAIL: no local player 5 s after the world loaded";
        }
    }
}
