using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimDiscordRelay.Report
{
    // Harmony patches for the in-game report window. Each one only does something while the window is open, so they cost
    // nothing the rest of the time. They are applied one by one by the plugin (a failing patch must not stop the others,
    // and must never stop the rest of the mod from loading).

    /// <summary>
    /// Registers the snapshot reply handler once the game (and with it the routed-RPC object of this connection) exists.
    /// </summary>
    [HarmonyPatch(typeof(Game), "Start")]
    internal static class ReportWindowRpcRegistrationPatch
    {
        private static void Postfix()
        {
            try
            {
                ReportWindowRpc.Register();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not register the report window RPC.", ex);
            }
        }
    }

    /// <summary>
    /// While the window is open the player does not walk, look around or swing a weapon (like with the inventory).
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class ReportWindowBlockControllerPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (ReportWindow.IsOpen)
                __result = false;
        }
    }

    /// <summary>
    /// Same for the player's own actions (hotbar, interact, ...).
    /// </summary>
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class ReportWindowBlockPlayerPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (ReportWindow.IsOpen)
                __result = false;
        }
    }

    /// <summary>
    /// The game hides and locks the mouse cursor while playing. While the window is open the cursor has to be free.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
    internal static class ReportWindowCursorPatch
    {
        private static bool Prefix()
        {
            if (!ReportWindow.IsOpen)
                return true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return false;
        }
    }

    /// <summary>
    /// Escape closes the window instead of opening the game menu.
    /// </summary>
    [HarmonyPatch(typeof(Menu), "Update")]
    internal static class ReportWindowEscapePatch
    {
        private static bool Prefix()
        {
            if (!ReportWindow.IsOpen)
                return true;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReportWindow.Close();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Scrolling a list in the window must not also zoom the camera. (Best effort: the window's own scrolling does not use
    /// this value, it reads UI Toolkit's wheel event directly.)
    /// </summary>
    [HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel")]
    internal static class ReportWindowScrollPatch
    {
        private static void Postfix(ref float __result)
        {
            if (ReportWindow.IsOpen)
                __result = 0f;
        }
    }
}
