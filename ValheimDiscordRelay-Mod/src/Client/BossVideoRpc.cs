using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Wire code for [Boss Death] Server - Video Source = Last Attacker.
    ///
    /// The boss video is cut from the screen frames of the game that records it, and only the game that OWNS the boss
    /// knows when it dies. To get the video from the player who landed the last hit, the owner asks that player's game
    /// to record it. Both messages are routed RPCs, so they travel client -> server -> client; the server needs no
    /// special handling for them.
    ///
    ///   Owner    -> Attacker  BossVideoRequest  (kind 0 = please record, kind 1 = cancel, I used my own video)
    ///   Attacker -> Owner     BossVideoReply    (0 = declined, 1 = accepted, 2 = video encoded and about to upload)
    ///
    /// The decisions themselves live in BossKillTracker (owner side) and ClientCaptureService.BossVideo (attacker side).
    /// Nothing here is trusted blindly: unknown protocol versions are dropped, and a reply only counts when it comes
    /// from the player that was asked.
    /// </summary>
    internal static class BossVideoRpc
    {
        private const int KindRequest = 0;
        private const int KindCancel = 1;

        internal const int StatusDeclined = 0;
        internal const int StatusAccepted = 1;
        internal const int StatusReady = 2;

        private const int MaxKeyChars = 64;
        private const int MaxNameChars = 64;

        private static ZRoutedRpc _registeredOn;

        /// <summary>
        /// Registers the handlers on the current ZRoutedRpc (a new one exists for every connection). Safe to call repeatedly.
        /// A dedicated server only ever owns bosses, it never records, so it gets the reply handler but not the request one.
        /// </summary>
        internal static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredOn))
                return;

            rpc.Register<ZPackage>(RelayProtocol.BossVideoReplyRpcName, OnReply);

            bool dedicated = ZNet.instance != null && ZNet.instance.IsDedicated();
            if (!dedicated)
                rpc.Register<ZPackage>(RelayProtocol.BossVideoRequestRpcName, OnRequest);

            _registeredOn = rpc;
            RelayDiagnostics.Debug("Registered boss video RPCs" + (dedicated ? " (reply only, dedicated server)." : "."));
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Asks the player with this peer id to record the boss video. Returns false if it could not be sent.</summary>
        internal static bool SendRequest(long target, string key, string prefab, string hoverName)
        {
            ZPackage package = new ZPackage();
            package.Write(RelayProtocol.ProtocolVersion);
            package.Write(KindRequest);
            package.Write(key ?? string.Empty);
            package.Write(prefab ?? string.Empty);
            package.Write(hoverName ?? string.Empty);

            return Send(target, RelayProtocol.BossVideoRequestRpcName, package);
        }

        /// <summary>Tells a player who accepted (or may still accept) that the owner is using its own video after all.</summary>
        internal static void SendCancel(long target, string key)
        {
            ZPackage package = new ZPackage();
            package.Write(RelayProtocol.ProtocolVersion);
            package.Write(KindCancel);
            package.Write(key ?? string.Empty);
            package.Write(string.Empty);
            package.Write(string.Empty);

            Send(target, RelayProtocol.BossVideoRequestRpcName, package);
        }

        /// <summary>Answers the boss owner. Must be called on the main thread.</summary>
        internal static void SendReply(long owner, string key, int status)
        {
            ZPackage package = new ZPackage();
            package.Write(RelayProtocol.ProtocolVersion);
            package.Write(key ?? string.Empty);
            package.Write(status);

            Send(owner, RelayProtocol.BossVideoReplyRpcName, package);
        }

        private static bool Send(long target, string rpcName, ZPackage package)
        {
            try
            {
                if (ZRoutedRpc.instance == null)
                    return false;

                ZRoutedRpc.instance.InvokeRoutedRPC(target, rpcName, new object[] { package });
                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not send a boss video message.", ex);
                return false;
            }
        }

        // ---------------------------------------------------------------- receiving

        /// <summary>
        /// Attacker side: the boss owner asks this game to record the boss video (or cancels an earlier request).
        /// </summary>
        private static void OnRequest(long sender, ZPackage package)
        {
            try
            {
                if (package.ReadInt() != RelayProtocol.ProtocolVersion)
                    return;

                int kind = package.ReadInt();
                string key = RelayProtocol.ReadSafeString(package, MaxKeyChars);
                string prefab = RelayProtocol.ReadSafeString(package, MaxNameChars);
                string hoverName = RelayProtocol.ReadSafeString(package, MaxNameChars);

                if (key.Length == 0 || sender == 0)
                    return;

                if (kind == KindCancel)
                {
                    BossKillTracker.MarkRemoteCancelled(key);
                    return;
                }

                if (kind != KindRequest)
                    return;

                bool accepted = TryAccept(sender, key, prefab, hoverName);
                SendReply(sender, key, accepted ? StatusAccepted : StatusDeclined);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to process a boss video request.", ex);
            }
        }

        private static bool TryAccept(long owner, string key, string prefab, string hoverName)
        {
            if (!BossConfig.ClientEnabled.Value || !BossConfig.Enabled.Value)
                return false;

            ClientCaptureService capture = ClientPlugin.CaptureService;
            if (capture == null)
                return false;

            BossInfo info;
            BossCatalog.TryGet(prefab, out info);

            string name = BossCatalog.StripTags(hoverName);
            string displayName = info != null ? info.DisplayName : (name.Length > 0 ? name : prefab);

            bool accepted = capture.BeginBossVideo(displayName, info != null ? info.AvatarUrl : null, key, owner);

            RelayDiagnostics.Info(accepted
                ? "The boss owner asked for the video of '" + displayName + "' (you landed the last hit); recording it."
                : "The boss owner asked for the video of '" + displayName + "', but this game cannot record it; the owner's video will be used.");

            return accepted;
        }

        /// <summary>
        /// Owner side: the player we asked answered. Only the player that was asked is believed.
        /// </summary>
        private static void OnReply(long sender, ZPackage package)
        {
            try
            {
                if (package.ReadInt() != RelayProtocol.ProtocolVersion)
                    return;

                string key = RelayProtocol.ReadSafeString(package, MaxKeyChars);
                int status = package.ReadInt();

                if (key.Length == 0)
                    return;

                BossKillTracker.OnRemoteReply(sender, key, status);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to process a boss video reply.", ex);
            }
        }
    }

    /// <summary>
    /// Registers the boss video RPCs once the game (and with it the routed-RPC object of this connection) exists.
    /// </summary>
    [HarmonyPatch(typeof(Game), "Start")]
    internal static class BossVideoRpcRegistrationPatch
    {
        private static void Postfix()
        {
            try
            {
                BossVideoRpc.Register();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not register the boss video RPCs.", ex);
            }
        }
    }
}
