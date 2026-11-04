using HarmonyLib;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Runs the death camera right after Valheim has positioned the camera for the frame.
/// </summary>
[HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
internal static class GameCameraDeathPatch
{
    private static void Postfix(GameCamera __instance)
    {
        DeathCamera.Apply(__instance);
    }
}
