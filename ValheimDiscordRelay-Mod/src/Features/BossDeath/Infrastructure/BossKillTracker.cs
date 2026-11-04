using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// Runs in the game that currently OWNS a boss (Valheim only executes damage and death on the owner).
/// Measures how much health each player took from the boss, batches that to the server, and reports the death.
/// When the owner is the server process itself (dedicated server or the local host), the reports are handed
/// straight to BossAnnouncer instead of going over an RPC.
///
/// The death report is what makes the server post the "{Boss} has fallen!" embed. When this client is also going
/// to upload a boss video, the report is held back until the video is encoded and about to upload, so the
/// announcement and the video show up in Discord together instead of seconds apart.
///
/// With [Boss Death] Server - Video Source = Last Attacker, the video is recorded by the player who landed the last hit
/// instead (see BossVideoRpc). This client then only remembers who that was, asks that player's game to record, and
/// holds the announcement until that game reports the video is ready. This client's own clip is kept as the fallback
/// and is only used when the last attacker cannot or does not answer in time.
/// </summary>
internal static partial class BossKillTracker
{
    private sealed class PendingBoss
    {
        public long User;
        public long Id;
        public string Prefab;
        public readonly Dictionary<string, float> Damage = new(StringComparer.Ordinal);
    }

    /// <summary>A boss death whose report is waiting for the boss video to be ready.</summary>
    private sealed class DeferredDeath
    {
        public long User;
        public long Id;
        public string Prefab;
        public string HoverName;
        public float Deadline;
    }

    private const float FlushIntervalSeconds = 1f;
    private const int MaxHandledRemembered = 32;

    /// <summary>
    /// Longest the announcement is held back. Covers the post-death recording (up to 4 s) plus frame staging and
    /// WebP encoding with a wide margin; if the video is not ready by then the announcement goes out without it.
    /// </summary>
    private const float DeferredDeathTimeoutSeconds = 75f;

    private static readonly Dictionary<string, DeferredDeath> Deferred = new(StringComparer.Ordinal);

    /// <summary>Keys released by the video worker thread; drained on the main thread in Tick.</summary>
    private static readonly ConcurrentQueue<string> Released = new();

    private static readonly Dictionary<string, PendingBoss> Pending = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal);
    private static readonly Queue<string> HandledOrder = new();
    private static float _nextFlushTime;

    /// <summary>
    /// The hit currently being applied to a tracked boss (between the RPC_Damage prefix and postfix).
    /// Killing a boss can destroy its object inside RPC_Damage, after which its health/ZDO can no longer be read,
    /// so the OnDeath patch finishes the job from this state when that happens.
    /// </summary>
    private sealed class HitState
    {
        public Character Boss;
        public HitData Hit;
        public float HealthBefore;
        public int Frame;
        public bool Finalized;
    }

    private static HitState _current;

    // ---------------------------------------------------------------- hooks called by the Harmony patches

    /// <summary>
    /// Called before Character.RPC_Damage: remembers the boss's health so the damage can be measured afterwards.
    /// Does nothing for characters that are not a tracked boss.
    /// </summary>
    internal static void BeginDamage(Character character, HitData hit)
    {
        _current = null;

        if (!IsTrackableBoss(character))
            return;

        _current = new HitState
        {
            Boss = character,
            Hit = hit,
            HealthBefore = character.GetHealth(),
            Frame = Time.frameCount
        };
    }

    /// <summary>
    /// Called after Character.RPC_Damage.
    /// </summary>
    internal static void EndDamage(Character boss)
    {
        HitState state = _current;
        _current = null;

        if (state == null || state.Finalized || !ReferenceEquals(state.Boss, boss))
            return;

        state.Finalized = true;
        ProcessHit(state, false);
    }

    /// <summary>
    /// Called when a character is about to die. For a boss killed by the hit that is being applied right now, this
    /// counts that killing blow and reports the death; otherwise it is a safety net for deaths that do not come
    /// through RPC_Damage.
    /// </summary>
    internal static void OnNativeDeath(Character character)
    {
        if (character == null || character is Player)
            return;

        HitState state = _current;
        if (state != null && !state.Finalized && ReferenceEquals(state.Boss, character) && state.Frame == Time.frameCount)
        {
            state.Finalized = true;
            ProcessHit(state, true);
            return;
        }

        if (!BossConfig.ClientOn || !BossConfig.On)
            return;

        bool isBoss;
        try { isBoss = character.IsBoss() && character.IsOwner(); }
        catch { return; }

        if (!isBoss)
            return;

        string key;
        long user;
        long id;
        string prefab;
        if (!TryGetIdentity(character, out key, out user, out id, out prefab))
            return;

        ReportDeath(character, key, user, id, prefab);
    }

    /// <summary>
    /// Credits the health this hit removed to the attacking player and reports the death if the boss died.
    /// </summary>
    /// <param name="state">The hit being applied.</param>
    /// <param name="dying">True when called from the OnDeath patch, i.e. the boss is certainly dying.</param>
    private static void ProcessHit(HitState state, bool dying)
    {
        Character boss = state.Boss;

        string key;
        long user;
        long id;
        string prefab;
        if (!TryGetIdentity(boss, out key, out user, out id, out prefab))
            return;

        float healthAfter = boss.GetHealth();

        ClientCaptureService capture = ValheimDiscordRelayPlugin.CaptureService;
        if (capture != null)
            capture.NoteBossEngaged();

        string player = ResolvePlayerAttacker(state.Hit);
        float dealt = Mathf.Max(0f, state.HealthBefore - Mathf.Max(0f, healthAfter));

        if (player != null && dealt > 0f)
            AddDamage(key, user, id, prefab, player, dealt);

        if (player != null && (dealt > 0f || dying))
            NoteLastAttacker(key, player, state.Hit);

        if (dying || healthAfter <= 0f)
            ReportDeath(boss, key, user, id, prefab);
    }

    /// <summary>
    /// Sends batched damage to the server about once per second.
    /// </summary>
    internal static void Tick()
    {
        ProcessDeferredDeaths();
        ProcessRemoteRequests();

        if (Pending.Count == 0)
            return;

        float now = Time.unscaledTime;
        if (now < _nextFlushTime)
            return;

        _nextFlushTime = now + FlushIntervalSeconds;

        List<PendingBoss> batch = new List<PendingBoss>(Pending.Values);
        Pending.Clear();

        foreach (PendingBoss pending in batch)
            SendDamage(pending);
    }

    /// <summary>
    /// Tells the tracker the boss video is ready (or will never be), so the held-back death report can go out.
    /// Safe to call from any thread, and more than once; the report itself is sent on the main thread.
    /// </summary>
    /// <param name="key">Boss identity the video belongs to.</param>
    internal static void ReleaseDeath(string key)
    {
        ReleaseDeath(key, 0);
    }

    /// <summary>
    /// Same, for a video recorded here on request of a boss owner (remoteOwner != 0): instead of releasing a local
    /// announcement, tells that owner the video is ready so it can release its own.
    /// </summary>
    /// <param name="key">Boss identity the video belongs to.</param>
    /// <param name="remoteOwner">Peer id of the boss owner, or 0 when the boss is owned by this client.</param>
    internal static void ReleaseDeath(string key, long remoteOwner)
    {
        if (string.IsNullOrEmpty(key))
            return;

        if (remoteOwner != 0)
            ReadyToSend.Enqueue(new KeyValuePair<long, string>(remoteOwner, key));
        else
            Released.Enqueue(key);
    }

    // ---------------------------------------------------------------- internals

    private static bool IsTrackableBoss(Character character)
    {
        if (character == null || character is Player)
            return false;

        if (!BossConfig.ClientOn || !BossConfig.On)
            return false;

        try
        {
            return character.IsBoss() && character.IsOwner() && !character.IsDead();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsServerProcess()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    internal static bool TryGetIdentity(Character boss, out string key, out long user, out long id, out string prefab)
    {
        key = null;
        user = 0;
        id = 0;
        prefab = null;

        try
        {
            ZNetView view = boss.GetComponent<ZNetView>();
            if (view == null || !view.IsValid())
                return false;

            ZDO zdo = view.GetZDO();
            if (zdo == null)
                return false;

            ZDOID zdoId = zdo.m_uid;
            user = zdoId.UserID;
            id = (long)zdoId.ID;
            prefab = BossCatalog.CleanPrefabName(boss.gameObject.name);
            key = BossAnnouncer.MakeKey(user, id);
            return true;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read boss identity: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Returns the attacking player's character name, or null if the hit did not come from a player.
    /// </summary>
    internal static string ResolvePlayerAttacker(HitData hit)
    {
        if (hit == null)
            return null;

        try
        {
            if (!hit.HaveAttacker())
                return null;

            Player player = hit.GetAttacker() as Player;
            if (player != null)
                return CleanPlayerName(player.GetPlayerName());

            // The attacker's object may not be loaded on this machine; players also store their name in their ZDO.
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null)
                return null;

            ZDO zdo = zdoMan.GetZDO(hit.m_attacker);
            if (zdo == null)
                return null;

            return CleanPlayerName(zdo.GetString(ZDOVars.s_playerName, string.Empty));
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not resolve boss attacker: " + ex.Message);
            return null;
        }
    }

    private static string CleanPlayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string cleaned = ClientTextUtils.SanitizeUsername(BossCatalog.StripTags(name));
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static void AddDamage(string key, long user, long id, string prefab, string player, float dealt)
    {
        if (IsServerProcess())
        {
            BossAnnouncer.AddDamage(key, prefab, player, dealt);
            return;
        }

        PendingBoss pending;
        if (!Pending.TryGetValue(key, out pending))
        {
            pending = new PendingBoss { User = user, Id = id, Prefab = prefab };
            Pending[key] = pending;
        }

        float existing;
        pending.Damage.TryGetValue(player, out existing);
        pending.Damage[player] = existing + dealt;
    }

    private static void ReportDeath(Character boss, string key, long user, long id, string prefab)
    {
        // Multi-phase bosses (Kall Fimbulbringer) die once per phase; only the final phase is a real kill.
        // The damage already dealt in this phase is still sent to the server and merged into the final announcement.
        if (BossCatalog.IsIntermediatePhase(prefab))
        {
            if (Handled.Add(key))
            {
                HandledOrder.Enqueue(key);
                while (HandledOrder.Count > MaxHandledRemembered)
                    Handled.Remove(HandledOrder.Dequeue());

                RelayDiagnostics.Info("Boss phase ended (prefab '" + prefab + "'); not announcing until the final phase dies.");
            }
            return;
        }

        if (!Handled.Add(key))
            return;

        HandledOrder.Enqueue(key);
        while (HandledOrder.Count > MaxHandledRemembered)
            Handled.Remove(HandledOrder.Dequeue());

        string hoverName = string.Empty;
        try { hoverName = boss.GetHoverName() ?? string.Empty; }
        catch { }
        hoverName = BossCatalog.StripTags(hoverName);

        RelayDiagnostics.Info("Boss death detected. Prefab='" + prefab + "', name='" + hoverName + "'.");

        BossInfo info;
        BossCatalog.TryGet(prefab, out info);

        // Start the video first: whether one is coming decides whether the announcement waits for it.
        // This client's own clip is always started; with Video Source = Last Attacker it is the fallback that is
        // dropped once the last attacker's game has taken the video over.
        ClientCaptureService capture = ValheimDiscordRelayPlugin.CaptureService;
        bool ownVideo = capture != null &&
                        capture.BeginBossVideo(info != null ? info.DisplayName : (hoverName.Length > 0 ? hoverName : prefab),
                                               info != null ? info.AvatarUrl : null,
                                               key);

        bool remoteVideo = TryRequestRemoteVideo(key, prefab, hoverName, ownVideo);
        bool videoComing = ownVideo || remoteVideo;

        if (!videoComing)
        {
            AnnounceDeath(key, user, id, prefab, hoverName);
            return;
        }

        Deferred[key] = new DeferredDeath
        {
            User = user,
            Id = id,
            Prefab = prefab,
            HoverName = hoverName,
            Deadline = Time.unscaledTime + DeferredDeathTimeoutSeconds
        };

        RelayDiagnostics.Info("Boss announcement is held until the boss video is ready (at most " +
                              (int)DeferredDeathTimeoutSeconds + " s).");
    }

    private static void SendDamage(PendingBoss pending)
    {
        if (pending == null || pending.Damage.Count == 0)
            return;

        ZPackage package = new ZPackage();
        package.Write(RelayProtocol.ProtocolVersion);
        package.Write(pending.User);
        package.Write(pending.Id);
        package.Write(pending.Prefab ?? string.Empty);
        package.Write(pending.Damage.Count);

        foreach (KeyValuePair<string, float> entry in pending.Damage)
        {
            package.Write(entry.Key);
            package.Write(entry.Value);
        }

        SendToServer(RelayProtocol.BossDamageRpcName, package);
    }

    private static void SendDeath(long user, long id, string prefab, string hoverName)
    {
        ZPackage package = new ZPackage();
        package.Write(RelayProtocol.ProtocolVersion);
        package.Write(user);
        package.Write(id);
        package.Write(prefab ?? string.Empty);
        package.Write(hoverName ?? string.Empty);

        SendToServer(RelayProtocol.BossDeathRpcName, package);
    }

    private static void SendToServer(string rpcName, ZPackage package)
    {
        try
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
                return;

            ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
            if (serverPeer == null)
                return;

            ZRoutedRpc.instance.InvokeRoutedRPC(
                serverPeer.m_uid,
                rpcName,
                new object[] { package });
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("Could not send boss report to the server.", ex);
        }
    }
}
