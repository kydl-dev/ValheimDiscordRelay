using System;
using KydlCore.Ui;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// WHAT the mod's menu shows (UI Toolkit, shared KydlCore.Ui look). Compiled into the mod AND shown by the Unity preview
    /// (Assets/ValheimDiscordRelay_UI/Preview/MenuPreview.cs: enter Play mode in the Preview scene / any scene with that component).
    /// Keep game types (Player, ZNet, ...) OUT of this folder: the Unity project does not have them. The game side (keys, input lock,
    /// game data) is src/Ui/MainMenu.cs in the mod; pass data in through Build's parameters.
    /// Ready-made pieces in this folder (from WhipItUp): TabBar (tabs under the title bar), ModelViewportView + ModelViewerRig (3D view of a
    /// model you can turn / move / zoom; game side: src/Ui/ViewerScene.Make), DetailsView + DetailGroup / DetailRow (stat blocks with quality columns).
    /// </summary>
    internal static class MainMenuView
    {
        /// Builds the window. `text` turns a text key into the shown text (game: the mod's localization, Unity: English).
        internal static VisualElement Build(Func<string, string> text, Action onClose)
        {
            var w = UiKit.Window(text("valheimdiscordrelay_menu_title"), text("valheimdiscordrelay_menu_footer"), onClose, out UiScroll body);
            var c = body.Content;
            c.Add(UiControls.Heading(text("valheimdiscordrelay_menu_section")));
            c.Add(UiControls.Note(text("valheimdiscordrelay_menu_note")));
            c.Add(UiControls.Toggle(text("valheimdiscordrelay_menu_example_switch"), true, on => UiLog.Verbose("MainMenuView: example switch " + (on ? "on" : "off"))));
            c.Add(UiControls.Row(text("valheimdiscordrelay_menu_example_slider"), UiControls.Slider(0f, 10f, 5f, "0.0", v => UiLog.Verbose("MainMenuView: example slider " + v))));
            c.Add(UiControls.Line(UiControls.Button(text("valheimdiscordrelay_menu_close"), onClose, true)));
            UiLog.Verbose("MainMenuView: built");
            return w;
        }
    }
}
