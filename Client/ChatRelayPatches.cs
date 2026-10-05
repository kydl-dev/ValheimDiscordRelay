using System;
using HarmonyLib;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Detects Valheim's own "I have arrived!" shout so it can be intercepted (see ChatSendTextPatch).
    /// Detection is done here on the client because only the client knows the text in its own language.
    /// </summary>
    internal static class ArrivalTextDetector
    {
        /// <summary>
        /// Checks whether a chat message is Valheim's own localized player-arrived shout.
        /// </summary>
        /// <param name="type">The chat message's talker type.</param>
        /// <param name="text">The chat message text.</param>
        /// <returns>True if this is the native arrival shout.</returns>
        internal static bool IsPlayerArrivedShout(Talker.Type type, string text)
        {
            if (type != Talker.Type.Shout || string.IsNullOrWhiteSpace(text))
                return false;

            string expected = text;
            try
            {
                if (Localization.instance != null)
                {
                    string localized = Localization.instance.Localize("$text_player_arrived");
                    if (!string.IsNullOrWhiteSpace(localized))
                        expected = localized;
                }
            }
            catch
            {
            }

            return string.Equals(text.Trim(), expected.Trim(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Relays locally-sent chat messages to the server.
    ///
    /// Valheim's own arrival shout ("I have arrived!") is handled according to [Player Notifications] Server - Enabled:
    ///   ON  - it is intercepted and dropped here, never sent to Discord. The server posts its own join message instead.
    ///   OFF - it is relayed like any other shout, through the normal chat webhook.
    /// </summary>
    [HarmonyPatch(typeof(Chat), "SendText")]
    internal static class ChatSendTextPatch
    {
        /// <summary>
        /// Forwards a locally-sent chat message to the server via RPC.
        /// </summary>
        /// <param name="type">The chat message's talker type.</param>
        /// <param name="text">The chat message text.</param>
        private static void Prefix(Talker.Type type, string text)
        {
            try
            {
                if (ClientPlugin.SuppressChatRelay)
                    return;

                if (!ClientConfig.ChatEnabled.Value)
                    return;

                if (!RelayProtocol.IsRelayedType(type))
                    return;

                if (string.IsNullOrEmpty(text))
                    return;

                // Valheim's own arrival shout: with Player Notifications on, ignore it (the server announces the join itself).
                // With it off, fall through and relay it as a normal shout.
                if (PlayerNotificationConfig.Enabled.Value && ArrivalTextDetector.IsPlayerArrivedShout(type, text))
                    return;

                if (ZNet.instance == null)
                    return;

                // A world hosted from Valheim's own menu: this game is the server, so there is no server peer to
                // send an RPC to (and the host is not in the peer list). Hand the message straight to the relay.
                if (ZNet.instance.IsServer())
                {
                    ValheimDiscordRelay.Server.ServerRelay.RelayHostChat(type, text);
                    return;
                }

                if (ZRoutedRpc.instance == null)
                    return;

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null)
                    return;

                string safeText = text;
                int max = ClientConfig.ChatMaxMessageLength.Value;
                if (safeText.Length > max)
                    safeText = safeText.Substring(0, max);

                ZPackage package = new ZPackage();
                package.Write(RelayProtocol.ProtocolVersion);
                package.Write((int)type);
                package.Write(safeText);

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    serverPeer.m_uid,
                    RelayProtocol.RpcName,
                    new object[] { package });
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Could not relay chat message.", ex);
            }
        }
    }
}
