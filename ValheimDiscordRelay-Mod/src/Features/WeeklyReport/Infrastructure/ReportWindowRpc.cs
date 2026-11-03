using System;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// Client side of the in-game report window's network traffic: asks the server for the current numbers and hands the
    /// answer to the window.
    ///
    ///   Window -> Server  SnapshotRequest  (just the protocol version)
    ///   Server -> Window  SnapshotReply    (see ReportSnapshotCodec)
    ///   Server -> Game    PeriodReset      (a new statistics period started; see ReportTracker.OnPeriodReset)
    ///
    /// When this game IS the server (the host of a world hosted from Valheim's own menu) nothing is sent: the numbers are
    /// read straight from ReportStats.
    /// </summary>
    internal static class ReportWindowRpc
    {
        private static ZRoutedRpc _registeredOn;

        /// <summary>
        /// Registers the reply handler on the current ZRoutedRpc (a new one exists for every connection). Safe to call
        /// repeatedly. A dedicated server never opens the window, so it does not register it.
        /// </summary>
        internal static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredOn))
                return;

            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                return;

            rpc.Register<ZPackage>(ReportProtocol.SnapshotReplyRpcName, OnReply);
            rpc.Register<ZPackage>(ReportProtocol.PeriodResetRpcName, OnPeriodReset);
            _registeredOn = rpc;

            // A new connection: this game knows nothing about what the server has recorded for the players it reports.
            ReportTracker.ResetBestHitMemory();

            RelayDiagnostics.Debug("[Report] Registered RPCs " + ReportProtocol.SnapshotReplyRpcName + " and " + ReportProtocol.PeriodResetRpcName);
        }

        /// <summary>
        /// Asks for the current numbers. The answer arrives later through ReportWindow.Receive (at once when this game is
        /// the server). Returns false if the request could not be sent, e.g. there is no connection.
        /// </summary>
        internal static bool RequestSnapshot()
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null)
                    return false;

                if (znet.IsServer())
                {
                    ReportWindow.Receive(ReportSnapshotCodec.BuildCurrent());
                    return true;
                }

                if (ZRoutedRpc.instance == null)
                    return false;

                ZNetPeer serverPeer = znet.GetServerPeer();
                if (serverPeer == null)
                    return false;

                ZPackage package = new ZPackage();
                package.Write(ReportProtocol.ProtocolVersion);

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    serverPeer.m_uid,
                    ReportProtocol.SnapshotRequestRpcName,
                    new object[] { package });

                return true;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Could not ask the server for the weekly report.", ex);
                return false;
            }
        }

        /// <summary>
        /// True when the routed message really comes from the server this game is connected to. Replies and period resets
        /// only ever come from the server; a message from any other peer is ignored. A game that is itself the server
        /// (a world hosted from Valheim's own menu) reads its numbers directly and accepts none.
        /// </summary>
        private static bool IsFromServer(long sender)
        {
            ZNet znet = ZNet.instance;
            if (znet == null || znet.IsServer())
                return false;

            ZNetPeer serverPeer = znet.GetServerPeer();
            return serverPeer != null && serverPeer.m_uid == sender;
        }

        /// <summary>
        /// The server started a new statistics period. Only the server may send it (IsFromServer); even a forged one
        /// could do no more than make this game send a few highest hits again, which the server ignores when they are not a
        /// new best.
        /// </summary>
        private static void OnPeriodReset(long sender, ZPackage package)
        {
            try
            {
                if (!IsFromServer(sender))
                    return;

                if (package.ReadInt() != ReportProtocol.ProtocolVersion)
                    return;

                ReportTracker.OnPeriodReset();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Failed to read the new-period message from the server.", ex);
            }
        }

        /// <summary>
        /// The server's answer. The window only accepts it shortly after it asked for one.
        /// </summary>
        private static void OnReply(long sender, ZPackage package)
        {
            try
            {
                if (!IsFromServer(sender))
                {
                    RelayDiagnostics.Debug("[Report] Ignored a report reply that did not come from the server (sender " + sender + ").");
                    return;
                }

                ReportWindow.Receive(ReportSnapshotCodec.Read(package));
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[Report] Failed to read the weekly report from the server.", ex);
                ReportWindow.Receive(null);
            }
        }
    }
}
