using System;
using HarmonyLib;
using KydlCore.Common;

namespace ValheimDiscordRelay.Server;

/// <summary>Starts the admin channel for each world / connection.</summary>
[HarmonyPatch(typeof(Game), "Start")]
internal static class AdminSettingsRegistrationPatch
{
    private static void Postfix()
    {
        try
        {
            AdminSettingsChannel.Register();
        }
        catch (Exception ex)
        {
            ModLog.Error("Could not register the admin settings channel. " + ex);
        }
    }
}
