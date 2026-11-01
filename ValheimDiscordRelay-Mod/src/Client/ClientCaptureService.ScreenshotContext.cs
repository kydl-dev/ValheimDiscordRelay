using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimDiscordRelay.Client
{
    internal sealed partial class ClientCaptureService
    {
        // ---------------------------------------------------------------------------------------------------------
        // Diagnostics context
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A one-line snapshot of the game state around a screenshot: is there a local player, is the game loading,
        /// teleporting or paused, and is the rolling capture alive. Distinguishes a screen that was genuinely black
        /// from a capture that went wrong.
        /// </summary>
        private string DescribeScreenshotContext()
        {
            string player;
            try
            {
                Player local = Player.m_localPlayer;
                player = local == null
                    ? "none (menu or loading)"
                    : "present (teleporting=" + local.IsTeleporting() + ", dead=" + local.IsDead() + ")";
            }
            catch (Exception ex)
            {
                player = "unknown (" + ex.GetType().Name + ")";
            }

            string world;
            try { world = ZNetScene.instance != null ? "loaded" : "not loaded"; }
            catch (Exception) { world = "unknown"; }

            string paused;
            try { paused = Game.IsPaused().ToString(); }
            catch (Exception) { paused = "unknown"; }

            float bufferedAge;
            FrameEntry buffered = GetNewestBufferedFrame(out bufferedAge);
            string bufferedText = buffered == null
                ? "none"
                : "age " + bufferedAge.ToString("0.00", CultureInfo.InvariantCulture) + "s";

            return "player=" + player +
                   ", world=" + world +
                   ", loadingScreen=" + DescribeLoadingScreen() +
                   ", paused=" + paused +
                   ", timeScale=" + Time.timeScale.ToString("0.##", CultureInfo.InvariantCulture) +
                   ", screen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode +
                   ", bufferedFrame=" + bufferedText;
        }

        /// <summary>
        /// Whether Valheim's loading screen is showing. Read by reflection (Hud.instance.m_loadingScreen) so a change in
        /// the game's UI classes can only make this say "unknown", never break the build or the screenshot.
        /// </summary>
        private static string DescribeLoadingScreen()
        {
            try
            {
                Type hudType = typeof(Player).Assembly.GetType("Hud");
                if (hudType == null)
                    return "unknown";

                const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                FieldInfo instanceField = hudType.GetField("instance", AnyStatic);
                object hud = instanceField != null ? instanceField.GetValue(null) : null;
                if (hud == null)
                    return "no HUD";

                FieldInfo screenField = hudType.GetField("m_loadingScreen", AnyInstance);
                object screen = screenField != null ? screenField.GetValue(hud) : null;
                if (screen == null)
                    return "unknown";

                GameObject go = screen as GameObject;
                if (go == null)
                {
                    Component component = screen as Component;
                    if (component != null)
                        go = component.gameObject;
                }

                if (go == null)
                    return "unknown";

                string state = go.activeInHierarchy ? "showing" : "hidden";

                PropertyInfo alpha = screen.GetType().GetProperty("alpha", AnyInstance);
                if (alpha != null && alpha.PropertyType == typeof(float))
                    state += " (alpha " + ((float)alpha.GetValue(screen, null)).ToString("0.00", CultureInfo.InvariantCulture) + ")";

                return state;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
