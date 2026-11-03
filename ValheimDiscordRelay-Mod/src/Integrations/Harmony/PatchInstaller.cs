using System;
using HarmonyLib;
using Kydl.Common;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>Applies the mod's Harmony patches (patching lives in Integrations/Harmony).</summary>
    internal static class PatchInstaller
    {
        /// <summary>Applies the patches the mod cannot work without: the server side and the player's game.</summary>
        /// <param name="harmony">The Harmony instance of this mod.</param>
        internal static void ApplyRequired(Harmony harmony)
        {
            // server side
            harmony.PatchAll(typeof(GameStartPatch));
            harmony.PatchAll(typeof(GameShutdownPatch));
            harmony.PatchAll(typeof(GameLogoutPatch));
            harmony.PatchAll(typeof(ZNetDisconnectPatch));
            harmony.PatchAll(typeof(ZNetShutdownPatch));
            harmony.PatchAll(typeof(AdminSettingsRegistrationPatch));
            harmony.PatchAll(typeof(ZNetAwakePatch));

            // player's game
            harmony.PatchAll(typeof(ChatSendTextPatch));
            harmony.PatchAll(typeof(PlayerDamagePatch));
            harmony.PatchAll(typeof(PlayerDeathStatePatch));
            harmony.PatchAll(typeof(PlayerOnDeathPatch));
            harmony.PatchAll(typeof(GameCameraDeathPatch));
            harmony.PatchAll(typeof(GameSessionShutdownPatch));
            harmony.PatchAll(typeof(BossDamagePatch));
            harmony.PatchAll(typeof(BossOnDeathPatch));
            harmony.PatchAll(typeof(BossVideoRpcRegistrationPatch));
            harmony.PatchAll(typeof(ReportDamagePatch));
            harmony.PatchAll(typeof(ReportCharacterDeathPatch));
        }

        /// <summary>
        /// Applies the patches of the weekly report window one at a time: they only matter while the window is open, so none of them may stop
        /// the plugin loading. One that cannot be applied (a game update renamed its target) is logged and skipped.
        /// </summary>
        /// <param name="harmony">The Harmony instance of this mod.</param>
        internal static void ApplyOptional(Harmony harmony)
        {
            Optional(harmony, typeof(ReportWindowRpcRegistrationPatch));
            Optional(harmony, typeof(ReportWindowBlockControllerPatch));
            Optional(harmony, typeof(ReportWindowBlockPlayerPatch));
            Optional(harmony, typeof(ReportWindowCursorPatch));
            Optional(harmony, typeof(ReportWindowEscapePatch));
            Optional(harmony, typeof(ReportWindowScrollPatch));
        }

        private static void Optional(Harmony harmony, Type patchClass)
        {
            try
            {
                harmony.PatchAll(patchClass);
            }
            catch (Exception ex)
            {
                ModLog.Error("PatchInstaller: could not apply the optional patch " + patchClass.Name + ". " + ex);
            }
        }
    }
}