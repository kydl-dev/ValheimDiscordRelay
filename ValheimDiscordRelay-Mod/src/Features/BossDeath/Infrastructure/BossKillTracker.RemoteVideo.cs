using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay.Client
{
    internal static partial class BossKillTracker
    {
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
    }
}
