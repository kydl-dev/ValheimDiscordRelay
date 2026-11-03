using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Valheim's own UI fonts, for the style sheet property <c>-unity-font-definition</c>.
    ///
    /// The game ships them as Unity <see cref="Font"/> assets (the names are the ones Jotunn's GUIManager looks up):
    ///   heading       Norse                      the rune-like display font used for window titles
    ///   heading-bold  Norsebold
    ///   body          AveriaSerifLibre-Regular   the serif text font of the game's menus
    ///   body-bold     AveriaSerifLibre-Bold
    ///
    /// A font that cannot be found is simply not applied (the style sheet then leaves the element on the window's own font),
    /// so a game update that renames a font costs the look, never the window.
    /// </summary>
    internal static class ReportFonts
    {
        private static readonly Dictionary<string, string> AssetNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "heading", "Norse" },
            { "heading-bold", "Norsebold" },
            { "body", "AveriaSerifLibre-Regular" },
            { "body-bold", "AveriaSerifLibre-Bold" },
        };

        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>(StringComparer.Ordinal);
        private static bool _searched;
        private static bool _loggedMissing;

        /// <summary>True for the keywords this class knows (heading, heading-bold, body, body-bold).</summary>
        internal static bool IsKeyword(string keyword)
        {
            return keyword != null && AssetNames.ContainsKey(keyword);
        }

        /// <summary>Forgets what was found, so the next style sheet load looks again (the window calls this when it reads its styles).</summary>
        internal static void Reset()
        {
            Cache.Clear();
            _searched = false;
        }

        /// <summary>The font for a keyword, or null when the game does not have it.</summary>
        internal static Font Get(string keyword)
        {
            if (!IsKeyword(keyword))
                return null;

            if (!_searched)
                Search();

            Font font;
            return Cache.TryGetValue(keyword, out font) ? font : null;
        }

        private static void Search()
        {
            _searched = true;

            try
            {
                Font[] all = Resources.FindObjectsOfTypeAll<Font>();

                foreach (KeyValuePair<string, string> pair in AssetNames)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && string.Equals(all[i].name, pair.Value, StringComparison.Ordinal))
                        {
                            // UI Toolkit builds its glyph atlas from a font file, so only dynamic (TrueType) fonts can be used.
                            if (!all[i].dynamic)
                            {
                                RelayDiagnostics.Warning("[Report] The font '" + pair.Value + "' is not a dynamic font and is not used.");
                                continue;
                            }

                            Cache[pair.Key] = all[i];
                            break;
                        }
                    }
                }

                if (Cache.Count < AssetNames.Count && !_loggedMissing)
                {
                    _loggedMissing = true;
                    RelayDiagnostics.Warning("[Report] Valheim fonts found: " + Cache.Count + " of " + AssetNames.Count +
                                             ". Text that wants a missing font keeps the window's default font.");
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("[Report] Valheim's fonts could not be looked up: " + ex.Message);
            }
        }
    }
}
