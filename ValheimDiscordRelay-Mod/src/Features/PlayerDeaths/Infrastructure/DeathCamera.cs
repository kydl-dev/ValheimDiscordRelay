using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// A short cinematic camera move for the seconds after the local player dies, so the death video does not end on
/// a static view of the corpse: the camera rises, keeps looking down at the body and, when a killer is known,
/// gradually turns towards it.
///
/// Valheim itself does nothing here: once the player is dead and the ragdoll exists, GameCamera.UpdateCamera only
/// calls LookAt(ragdoll) from wherever the camera happened to be. This class runs right after that (see
/// <see cref="GameCameraDeathPatch"/>) and overwrites the camera pose, so the move is in the real game view and
/// therefore in the recorded frames. It is only started while a death video is being recorded.
/// </summary>
internal static class DeathCamera
{
    /// <summary>Share of the post-death recording window the move takes; the rest is a short hold on the final shot.</summary>
    private const float TravelFraction = 0.85f;

    /// <summary>Share of the move spent easing from Valheim's own death view onto the new shot.</summary>
    private const float SettleFraction = 0.3f;

    /// <summary>Gap kept between the camera and a ceiling, tree branch or roof above it.</summary>
    private const float ObstacleMargin = 0.6f;

    private const float ObstacleProbeRadius = 0.3f;

    /// <summary>A killer further away than this is ignored: turning towards it would only lose the body from view.</summary>
    private const float MaxKillerDistance = 60f;

    /// <summary>
    /// How far the view may turn away from the body towards the killer, as a share of the narrower field of view.
    /// Keeps the body inside the frame even when the killer is off to the side.
    /// </summary>
    private const float MaxTurnOfFieldOfView = 0.4f;

    /// <summary>The player is normally flagged dead within a frame or two of the death signal; give up after this.</summary>
    private const float DeadFlagTimeoutSeconds = 3f;

    /// <summary>Safety net only (a respawn normally ends the move long before): ending the move snaps the camera back to where it was.</summary>
    private const float MaxLifetimeSeconds = 120f;

    private static bool _active;
    private static bool _haveStart;
    private static Player _player;
    private static AttackerInfo _killer;
    private static float _beginTime;
    private static float _duration;
    private static float _rise;
    private static Vector3 _startPosition;
    private static Quaternion _startRotation;
    private static Vector3 _bodyPosition;
    private static bool _haveBody;
    private static Vector3 _killerPosition;
    private static bool _haveKiller;
    private static Camera _camera;
    private static bool _loggedFailure;

    /// <summary>
    /// Starts the move for the death that just happened. Does nothing when the death camera is turned off.
    /// </summary>
    /// <param name="killer">Who killed the player (as named in the death message), or null.</param>
    /// <param name="postDurationSeconds">Length of the post-death part of the video; the move is timed to fit it.</param>
    internal static void Begin(AttackerInfo killer, float postDurationSeconds)
    {
        try
        {
            if (!ClientConfig.DeathCameraEnabled.Value)
                return;

            Player player = Player.m_localPlayer;
            if (player == null)
                return;

            _player = player;
            _killer = ClientConfig.DeathCameraLookAtKiller.Value ? killer : null;
            _haveKiller = _killer != null && _killer.HasPosition;
            _killerPosition = _haveKiller ? _killer.Position : Vector3.zero;
            _haveBody = false;
            _haveStart = false;
            _beginTime = Time.unscaledTime;
            _duration = Mathf.Max(1f, postDurationSeconds * TravelFraction);
            _rise = Mathf.Clamp(ClientConfig.DeathCameraHeight.Value, 1f, 15f);
            _loggedFailure = false;
            _active = true;

            RelayDiagnostics.Debug(
                "[DeathCamera] Started. Rise=" + _rise + " m over " + _duration.ToString("0.0") + " s, killer=" +
                (_killer == null ? "none" : "'" + _killer.Name + "'") + ".");
        }
        catch (Exception ex)
        {
            _active = false;
            RelayDiagnostics.Error("Could not start the death camera.", ex);
        }
    }

    internal static void Stop()
    {
        _active = false;
        _player = null;
        _killer = null;
        _camera = null;
    }

    /// <summary>
    /// Called after every GameCamera.UpdateCamera. Replaces the camera pose while a death move is running.
    /// </summary>
    internal static void Apply(GameCamera gameCamera)
    {
        if (!_active)
            return;

        try
        {
            float elapsed = Time.unscaledTime - _beginTime;

            Player player = Player.m_localPlayer;
            if (gameCamera == null ||
                SessionLifecycle.IsTearingDown ||
                elapsed > MaxLifetimeSeconds ||
                player == null ||
                player != _player ||
                GameCamera.InFreeFly())
            {
                // Respawned (a new Player object), left the world, or the camera was taken over by free-fly.
                Stop();
                return;
            }

            if (!player.IsDead())
            {
                // Valheim sets the flag a moment after the death signal; until then its own camera is in charge.
                if (elapsed > DeadFlagTimeoutSeconds)
                    Stop();

                return;
            }

            Transform cameraTransform = gameCamera.transform;

            if (!_haveStart)
            {
                // Start from whatever Valheim just produced, so the move begins without a jump.
                _startPosition = cameraTransform.position;
                _startRotation = cameraTransform.rotation;
                _rise = ClearanceAbove(gameCamera, _startPosition, _rise);
                _camera = gameCamera.GetComponentInChildren<Camera>();
                _haveStart = true;
            }

            float progress = Mathf.Clamp01(elapsed / _duration);
            float eased = SmoothStep(progress);

            Vector3 body = ResolveBodyPosition(player);
            RefreshKillerPosition();

            Vector3 position = _startPosition + Vector3.up * (_rise * eased);
            Vector3 toBody = body - position;
            if (toBody.sqrMagnitude < 0.01f)
                return;

            Vector3 lookDirection = toBody.normalized;

            if (_haveKiller)
            {
                Vector3 toKiller = _killerPosition - position;
                float killerDistanceSqr = toKiller.sqrMagnitude;

                if (killerDistanceSqr > 0.01f && killerDistanceSqr <= MaxKillerDistance * MaxKillerDistance)
                {
                    Vector3 killerDirection = toKiller.normalized;
                    float turn = Mathf.Min(
                        Vector3.Angle(lookDirection, killerDirection),
                        MaxTurnOfFieldOfView * NarrowFieldOfView());

                    lookDirection = Vector3.RotateTowards(
                        lookDirection, killerDirection, turn * Mathf.Deg2Rad * eased, 0f);
                }
            }

            Quaternion target = Quaternion.LookRotation(lookDirection, Vector3.up);
            float settle = SmoothStep(Mathf.Clamp01(elapsed / (_duration * SettleFraction)));

            cameraTransform.position = position;
            cameraTransform.rotation = Quaternion.Slerp(_startRotation, target, settle);
        }
        catch (Exception ex)
        {
            // Never let a camera problem break the game's render loop: log once and hand the camera back.
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                RelayDiagnostics.Error("Death camera failed; handing the camera back to Valheim.", ex);
            }

            Stop();
        }
    }

    /// <summary>
    /// The most the camera can rise from <paramref name="origin"/> before it would run into something above it.
    /// </summary>
    private static float ClearanceAbove(GameCamera gameCamera, Vector3 origin, float desired)
    {
        RaycastHit hit;
        if (Physics.SphereCast(
                origin, ObstacleProbeRadius, Vector3.up, out hit, desired + ObstacleMargin,
                gameCamera.m_blockCameraMask, QueryTriggerInteraction.Ignore))
        {
            return Mathf.Max(0f, hit.distance - ObstacleMargin);
        }

        return desired;
    }

    /// <summary>
    /// Where to look for the body: the ragdoll once it exists (exactly what Valheim's own death view looks at),
    /// the player's centre until then.
    /// </summary>
    private static Vector3 ResolveBodyPosition(Player player)
    {
        Ragdoll ragdoll = player.GetRagdoll();
        if (ragdoll != null)
        {
            _bodyPosition = ragdoll.GetAverageBodyPosition();
            _haveBody = true;
        }
        else if (!_haveBody)
        {
            _bodyPosition = player.GetCenterPoint();
        }

        return _bodyPosition;
    }

    /// <summary>
    /// Follows the killer while it is still in the world; once it is gone (killed, despawned) its last known
    /// position is kept.
    /// </summary>
    private static void RefreshKillerPosition()
    {
        if (_killer == null || ZNetScene.instance == null)
            return;

        GameObject instance = ZNetScene.instance.FindInstance(_killer.AttackerId);
        if (instance == null)
            return;

        Character character = instance.GetComponent<Character>();
        if (character == null)
            return;

        _killerPosition = character.GetCenterPoint();
        _haveKiller = true;
    }

    /// <summary>The smaller of the vertical and horizontal field of view, in degrees.</summary>
    private static float NarrowFieldOfView()
    {
        Camera camera = _camera != null ? _camera : Camera.main;
        if (camera == null)
            return 60f;

        float vertical = camera.fieldOfView;
        float horizontal = Mathf.Atan(Mathf.Tan(vertical * 0.5f * Mathf.Deg2Rad) * camera.aspect) * 2f * Mathf.Rad2Deg;
        return Mathf.Min(vertical, horizontal);
    }

    private static float SmoothStep(float t)
    {
        return t * t * (3f - 2f * t);
    }
}
