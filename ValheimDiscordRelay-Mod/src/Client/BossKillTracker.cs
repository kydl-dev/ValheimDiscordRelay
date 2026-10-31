using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Client
{
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
    internal static class BossKillTracker
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

        private sealed class LastAttackerInfo
        {
            public string Name;
            public long Uid;
            public float Time;
        }

        private enum RemoteState
        {
            Pending,
            Accepted,
            Declined
        }

        /// <summary>A request to the last attacker's game to record the boss video.</summary>
        private sealed class RemoteRequest
        {
            public long Target;
            public string TargetName;
            public RemoteState State;

            /// <summary>True when this client is also recording its own clip (the fallback).</summary>
            public bool OwnVideo;

            public float ReplyDeadline;
            public float ExpiresAt;
        }

        /// <summary>A last attacker whose hit is older than this before the kill is not trusted to be facing the boss.</summary>
        private const float MaxLastAttackerAgeSeconds = 20f;

        private const int MaxTrackedAttackers = 32;

        /// <summary>How long to wait for the last attacker's answer when this client has no clip of its own to fall back on.</summary>
        private const float RemoteReplyTimeoutSeconds = 6f;

        private const float RemoteEntryLifetimeSeconds = 180f;

        /// <summary>Last player hit seen per boss, only tracked while Video Source is Last Attacker.</summary>
        private static readonly Dictionary<string, LastAttackerInfo> LastAttackers = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, RemoteRequest> Remote = new(StringComparer.Ordinal);

        /// <summary>(boss owner, key) pairs the video worker thread wants to answer; drained on the main thread in Tick.</summary>
        private static readonly ConcurrentQueue<KeyValuePair<long, string>> ReadyToSend = new();

        /// <summary>Attacker side: boss owners that cancelled their request because they used their own video.</summary>
        private static readonly HashSet<string> RemoteCancelled = new(StringComparer.Ordinal);

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

            if (!BossConfig.ClientEnabled.Value || !BossConfig.Enabled.Value)
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

            ClientCaptureService capture = ClientPlugin.CaptureService;
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

        // ---------------------------------------------------------------- Video Source = Last Attacker

        /// <summary>
        /// Owner side, main thread: true while the last attacker has been asked but has not answered yet.
        /// </summary>
        internal static bool IsRemoteReplyPending(string key)
        {
            RemoteRequest request;
            return Remote.TryGetValue(key, out request) && request.State == RemoteState.Pending;
        }

        /// <summary>
        /// Owner side, main thread: called when this client's own clip is ready. True if the last attacker's game has
        /// taken the video over, so this client must not upload its own. If the attacker has not answered by now it is
        /// told to stand down and this client's clip is used (so there is never a second video).
        /// </summary>
        internal static bool IsVideoTakenOver(string key)
        {
            RemoteRequest request;
            if (!Remote.TryGetValue(key, out request))
                return false;

            if (request.State == RemoteState.Accepted)
                return true;

            if (request.State == RemoteState.Pending)
            {
                request.State = RemoteState.Declined;
                BossVideoRpc.SendCancel(request.Target, key);
                RelayDiagnostics.Info("The last attacker did not answer in time; using the boss owner's video.");
            }

            return false;
        }

        /// <summary>
        /// Owner side: an answer from the player we asked to record the video.
        /// </summary>
        internal static void OnRemoteReply(long sender, string key, int status)
        {
            RemoteRequest request;
            if (!Remote.TryGetValue(key, out request) || request.Target != sender)
                return;

            if (status == BossVideoRpc.StatusAccepted)
            {
                if (request.State == RemoteState.Pending)
                {
                    request.State = RemoteState.Accepted;
                    RelayDiagnostics.Info("'" + request.TargetName + "' (last attacker) is recording the boss video.");
                }
            }
            else if (status == BossVideoRpc.StatusDeclined)
            {
                if (request.State == RemoteState.Pending)
                {
                    request.State = RemoteState.Declined;
                    RelayDiagnostics.Info("'" + request.TargetName + "' (last attacker) cannot record the boss video; using the boss owner's video.");

                    // With no clip of our own to wait for, there is nothing left to hold the announcement for.
                    if (!request.OwnVideo)
                        SendDeferredDeath(key);
                }
            }
            else if (status == BossVideoRpc.StatusReady)
            {
                if (request.State != RemoteState.Declined)
                {
                    request.State = RemoteState.Accepted;
                    SendDeferredDeath(key);
                }
            }
        }

        /// <summary>
        /// Attacker side: the boss owner took its own video after all, so this game must not upload one.
        /// </summary>
        internal static void MarkRemoteCancelled(string key)
        {
            lock (RemoteCancelled)
            {
                if (RemoteCancelled.Count >= 16)
                    RemoteCancelled.Clear();

                RemoteCancelled.Add(key);
            }
        }

        internal static bool IsRemoteCancelled(string key)
        {
            lock (RemoteCancelled)
            {
                return RemoteCancelled.Contains(key);
            }
        }

        internal static void ClearRemoteCancelled(string key)
        {
            lock (RemoteCancelled)
            {
                RemoteCancelled.Remove(key);
            }
        }

        private static void NoteLastAttacker(string key, string name, HitData hit)
        {
            if (!BossConfig.UseLastAttackerVideo || hit == null)
                return;

            long uid;
            try { uid = hit.m_attacker.UserID; }
            catch { return; }

            if (uid == 0)
                return;

            float now = Time.unscaledTime;

            if (LastAttackers.Count >= MaxTrackedAttackers)
            {
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, LastAttackerInfo> pair in LastAttackers)
                {
                    if (now - pair.Value.Time > 600f)
                        stale.Add(pair.Key);
                }

                foreach (string staleKey in stale)
                    LastAttackers.Remove(staleKey);

                if (LastAttackers.Count >= MaxTrackedAttackers)
                    LastAttackers.Clear();
            }

            LastAttackers[key] = new LastAttackerInfo { Name = name, Uid = uid, Time = now };
        }

        private static bool IsLocalPlayerUid(long uid)
        {
            try
            {
                Player local = Player.m_localPlayer;
                if (local == null)
                    return false;

                ZNetView view = local.GetComponent<ZNetView>();
                if (view == null || !view.IsValid())
                    return false;

                ZDO zdo = view.GetZDO();
                return zdo != null && zdo.m_uid.UserID == uid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// With Video Source = Last Attacker, asks the player who landed the last hit to record the video.
        /// Returns true if a request went out (the announcement is then held until it is settled).
        /// </summary>
        private static bool TryRequestRemoteVideo(string key, string prefab, string hoverName, bool ownVideo)
        {
            if (!BossConfig.UseLastAttackerVideo)
                return false;

            LastAttackerInfo attacker;
            if (!LastAttackers.TryGetValue(key, out attacker))
            {
                RelayDiagnostics.Info("Video Source is Last Attacker, but no player hit was recorded here; using the boss owner's video.");
                return false;
            }

            LastAttackers.Remove(key);

            if (Time.unscaledTime - attacker.Time > MaxLastAttackerAgeSeconds)
            {
                RelayDiagnostics.Info("The last player hit was more than " + (int)MaxLastAttackerAgeSeconds +
                                      " s before the kill; using the boss owner's video.");
                return false;
            }

            if (IsLocalPlayerUid(attacker.Uid))
                return false; // this client landed the last hit: its own video already is the last attacker's video

            if (!BossVideoRpc.SendRequest(attacker.Uid, key, prefab, hoverName))
                return false;

            float now = Time.unscaledTime;
            Remote[key] = new RemoteRequest
            {
                Target = attacker.Uid,
                TargetName = attacker.Name,
                State = RemoteState.Pending,
                OwnVideo = ownVideo,
                ReplyDeadline = now + RemoteReplyTimeoutSeconds,
                ExpiresAt = now + RemoteEntryLifetimeSeconds
            };

            RelayDiagnostics.Info("Asked '" + attacker.Name + "' (last attacker) to record the boss video.");
            return true;
        }

        private static void ProcessRemoteRequests()
        {
            KeyValuePair<long, string> ready;
            while (ReadyToSend.TryDequeue(out ready))
                BossVideoRpc.SendReply(ready.Key, ready.Value, BossVideoRpc.StatusReady);

            if (Remote.Count == 0)
                return;

            float now = Time.unscaledTime;
            List<string> noAnswer = null;
            List<string> expired = null;

            foreach (KeyValuePair<string, RemoteRequest> pair in Remote)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    if (expired == null)
                        expired = new List<string>();
                    expired.Add(pair.Key);
                }
                else if (pair.Value.State == RemoteState.Pending && !pair.Value.OwnVideo && now >= pair.Value.ReplyDeadline)
                {
                    if (noAnswer == null)
                        noAnswer = new List<string>();
                    noAnswer.Add(pair.Key);
                }
            }

            if (noAnswer != null)
            {
                foreach (string key in noAnswer)
                {
                    RemoteRequest request = Remote[key];
                    request.State = RemoteState.Declined;
                    BossVideoRpc.SendCancel(request.Target, key);

                    RelayDiagnostics.Warning("The last attacker did not answer the boss video request; sending the boss announcement without a video.");
                    SendDeferredDeath(key);
                }
            }

            if (expired != null)
            {
                foreach (string key in expired)
                    Remote.Remove(key);
            }
        }

        /// <summary>
        /// Sends every held-back death report immediately (used when the game session is being torn down).
        /// </summary>
        internal static void FlushDeferredDeaths()
        {
            if (Deferred.Count == 0)
                return;

            foreach (string key in new List<string>(Deferred.Keys))
                SendDeferredDeath(key);
        }

        private static void ProcessDeferredDeaths()
        {
            string releasedKey;
            while (Released.TryDequeue(out releasedKey))
                SendDeferredDeath(releasedKey);

            if (Deferred.Count == 0)
                return;

            float now = Time.unscaledTime;
            List<string> overdue = null;

            foreach (KeyValuePair<string, DeferredDeath> pair in Deferred)
            {
                if (now >= pair.Value.Deadline)
                {
                    if (overdue == null)
                        overdue = new List<string>();
                    overdue.Add(pair.Key);
                }
            }

            if (overdue == null)
                return;

            foreach (string key in overdue)
            {
                RelayDiagnostics.Warning("The boss video was not ready in time; sending the boss announcement without waiting for it.");
                SendDeferredDeath(key);
            }
        }

        private static void SendDeferredDeath(string key)
        {
            DeferredDeath death;
            if (!Deferred.TryGetValue(key, out death))
                return;

            Deferred.Remove(key);
            AnnounceDeath(key, death.User, death.Id, death.Prefab, death.HoverName);
        }

        /// <summary>
        /// Hands the boss death to the server so it posts the announcement: directly when this process is the server,
        /// otherwise as RPCs (the damage still pending for this boss first, then the death, on the same connection).
        /// </summary>
        private static void AnnounceDeath(string key, long user, long id, string prefab, string hoverName)
        {
            if (IsServerProcess())
            {
                BossAnnouncer.HandleDeath(key, prefab, hoverName);
                return;
            }

            PendingBoss pending;
            if (Pending.TryGetValue(key, out pending))
            {
                Pending.Remove(key);
                SendDamage(pending);
            }

            SendDeath(user, id, prefab, hoverName);
        }

        // ---------------------------------------------------------------- internals

        private static bool IsTrackableBoss(Character character)
        {
            if (character == null || character is Player)
                return false;

            if (!BossConfig.ClientEnabled.Value || !BossConfig.Enabled.Value)
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
            ClientCaptureService capture = ClientPlugin.CaptureService;
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

    /// <summary>
    /// Measures how much of the boss's health each hit removes (the real amount after resistances).
    /// Character.RPC_Damage only runs on the boss's owner.
    /// </summary>
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class BossDamagePatch
    {
        private static void Prefix(Character __instance, HitData hit)
        {
            try
            {
                BossKillTracker.BeginDamage(__instance, hit);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not start boss damage tracking.", ex);
            }
        }

        private static void Postfix(Character __instance)
        {
            try
            {
                BossKillTracker.EndDamage(__instance);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not track boss damage.", ex);
            }
        }
    }

    /// <summary>
    /// Fallback boss-death trigger for deaths that do not pass through RPC_Damage.
    /// </summary>
    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class BossOnDeathPatch
    {
        private static void Prefix(Character __instance)
        {
            try
            {
                BossKillTracker.OnNativeDeath(__instance);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not process boss death.", ex);
            }
        }
    }
}
