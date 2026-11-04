using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>What a manual screenshot shows.</summary>
internal enum ScreenshotMode
{
    /// <summary>The screen as the player sees it, HUD included.</summary>
    Normal = 0,

    /// <summary>The HUD is hidden the way Ctrl+F3 hides it.</summary>
    HudHidden = 1,

    /// <summary>The HUD and the player's own character are hidden.</summary>
    HudAndPlayerHidden = 2,
}

/// <summary>
/// Hides the HUD (and optionally the local player's character) for the length of one screenshot and puts everything back
/// exactly as it was. Safe to call <see cref="Restore"/> at any time, any number of times.
/// </summary>
internal sealed class ScreenshotSceneHider
{
    // Ctrl+F3 in Valheim flips Hud.m_userHidden; setting it is the same thing as pressing the key.
    private static readonly FieldInfo UserHiddenField =
        typeof(Hud).GetField("m_userHidden", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
    private bool _hudChanged;
    private bool _hudWasUserHidden;

    /// <summary>True between <see cref="Apply"/> and <see cref="Restore"/>.</summary>
    internal bool Active { get; private set; }

    /// <summary>
    /// Hides what <paramref name="mode"/> asks for. Returns false (and changes nothing) when the HUD cannot be hidden,
    /// so the caller can skip the screenshot instead of sending a picture that is not what was asked for.
    /// </summary>
    internal bool Apply(ScreenshotMode mode)
    {
        Restore();

        if (mode == ScreenshotMode.Normal)
            return true;

        try
        {
            if (Hud.instance == null || UserHiddenField == null)
                return false;

            _hudWasUserHidden = (bool)UserHiddenField.GetValue(Hud.instance);
            UserHiddenField.SetValue(Hud.instance, true);
            _hudChanged = true;
            Active = true;

            if (mode == ScreenshotMode.HudAndPlayerHidden)
                HidePlayer();

            return true;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not hide the HUD for a screenshot.", ex);
            Restore();
            return false;
        }
    }

    /// <summary>Puts the HUD and the player back. Does nothing when nothing is hidden.</summary>
    internal void Restore()
    {
        if (!Active && !_hudChanged && _hiddenRenderers.Count == 0)
            return;

        try
        {
            if (_hudChanged && Hud.instance != null && UserHiddenField != null)
                UserHiddenField.SetValue(Hud.instance, _hudWasUserHidden);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not bring the HUD back after a screenshot.", ex);
        }

        foreach (Renderer renderer in _hiddenRenderers)
        {
            // A renderer the game destroyed in the meantime (an unequipped item) compares equal to null.
            if (renderer != null)
                renderer.enabled = true;
        }

        _hiddenRenderers.Clear();
        _hudChanged = false;
        Active = false;
    }

    private void HidePlayer()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
            return;

        foreach (Renderer renderer in player.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer == null || !renderer.enabled)
                continue;

            renderer.enabled = false;
            _hiddenRenderers.Add(renderer);
        }
    }
}
