using System.Collections.Generic;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// Player-facing text of the mod, English only (user rule: no other languages). Shared by the game (registered with Jotunn in Plugin.Awake through KydlCore.Common.Loc)
    /// and the Unity preview (English). Every key starts with "valheimdiscordrelay_" so it never clashes with another mod.
    /// </summary>
    internal static class UiText
    {
        internal static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "valheimdiscordrelay_menu_title", "ValheimDiscordRelay" },
            { "valheimdiscordrelay_menu_footer", "Esc to close" },
            { "valheimdiscordrelay_menu_section", "Example" },
            { "valheimdiscordrelay_menu_note", "This is the starting menu of the mod. Replace its content in MainMenuView." },
            { "valheimdiscordrelay_menu_example_switch", "Example switch" },
            { "valheimdiscordrelay_menu_example_slider", "Example value" },
            { "valheimdiscordrelay_menu_close", "Close" },
        };


        /// English text (the Unity preview; the game reads the same table through Jotunn).
        internal static string English(string key)
        {
            string s;
            return En.TryGetValue(key, out s) ? s : key;
        }
    }
}
