using System;
using UnityEngine;

namespace ValheimDiscordRelay.Ui
{
    /// <summary>
    /// What a ModelViewportView talks to: something that puts one model in front of a camera and renders it into a texture (from WhipItUp's
    /// IWeaponScene, shared through the mod template). The Unity preview and the game both use ModelViewerRig; they differ only in how the model
    /// is made. Dispose removes the camera, lights, texture and model.
    /// </summary>
    internal interface IModelScene : IDisposable
    {
        /// The picture the viewport shows (null until Resize made it).
        Texture Texture { get; }

        /// Shows the model with that id (removes the one before). Expected outcome: returns whether a model is shown.
        bool Show(string modelId);

        /// The viewport's size in screen pixels; makes a new texture when it changed. Expected outcome: returns whether a texture exists.
        bool Resize(int widthPx, int heightPx);

        /// Renders once from that view. `pan` = where the camera looks, as an offset from the model's centre in world axes, in view heights at
        /// zoom 1 (ViewerOrbit.PanX / Y / Z). Expected outcome: returns whether something was rendered.
        bool Render(float yawDeg, float pitchDeg, float zoom, float panX, float panY, float panZ);
    }
}
