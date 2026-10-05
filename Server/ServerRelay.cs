using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ValheimDiscordRelay.Report;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Server-side relay orchestration: RPC registration, chat relay and lifecycle notifications.
    /// (Join/leave messages are in PlayerNotifications, the weekly report in the Report folder.)
    /// </summary>
    internal static class ServerRelay
    {
        private static ZRoutedRpc _registeredRpc;
        private static WebhookWorker _worker;
        private static bool _serverActive;
        private static string _restartMarkerPath;

        /// <summary>
        /// Per-attempt HTTP timeout for the blocking lifecycle send (see NotifyServerLifecycle).
        /// </summary>
        private const int LifecycleSendTimeoutMs = 4000;

        /// <summary>
        /// Called once from ServerPlugin.Awake(), after SharedConfig.InitAll().
        /// </summary>
        internal static void Initialize()
        {
            _restartMarkerPath = Path.Combine(BepInEx.Paths.CachePath, "ValheimDiscordRelay.clean-shutdown");

            ServerConfig.Enabled.SettingChanged += OnRelaySettingChanged;
            ServerConfig.ServerNotificationsEnabled.SettingChanged += OnRelaySettingChanged;
            BossConfig.Enabled.SettingChanged += OnRelaySettingChanged;
            PlayerNotificationConfig.Enabled.SettingChanged += OnRelaySettingChanged;
            ReportConfig.Enabled.SettingChanged += OnRelaySettingChanged;
        }

        /// <summary>
        /// Called once from ServerPlugin.OnDestroy().
        /// </summary>
        internal static void Shutdown()
        {
            if (ServerConfig.Enabled != null)
                ServerConfig.Enabled.SettingChanged -= OnRelaySettingChanged;
            if (ServerConfig.ServerNotificationsEnabled != null)
                ServerConfig.ServerNotificationsEnabled.SettingChanged -= OnRelaySettingChanged;
            if (BossConfig.Enabled != null)
                BossConfig.Enabled.SettingChanged -= OnRelaySettingChanged;
            if (PlayerNotificationConfig.Enabled != null)
                PlayerNotificationConfig.Enabled.SettingChanged -= OnRelaySettingChanged;
            if (ReportConfig.Enabled != null)
                ReportConfig.Enabled.SettingChanged -= OnRelaySettingChanged;

            WebhookWorker worker = _worker;
            if (worker != null)
                worker.Stop();

            _worker = null;
            _registeredRpc = null;
            _serverActive = false;
        }

        private static void OnRelaySettingChanged(object sender, EventArgs args)
        {
            RegisterRpc();
        }

        internal static void RegisterRpc()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            if (_worker == null && (ServerConfig.Enabled.Value ||
                                    ServerConfig.ServerNotificationsEnabled.Value ||
                                    BossConfig.Enabled.Value ||
                                    PlayerNotificationConfig.Enabled.Value ||
                                    ReportConfig.Enabled.Value))
            {
                _worker = new WebhookWorker();
                _worker.Start();
                _serverActive = true;
            }

            if (ZRoutedRpc.instance == null)
                return;

            if (_registeredRpc == ZRoutedRpc.instance)
                return;

            ZRoutedRpc.instance.Register<ZPackage>(RelayProtocol.RpcName, new Action<long, ZPackage>(OnClientChat));
            RelayDiagnostics.Info("Registered RPC " + RelayProtocol.RpcName);
            BossAnnouncer.Register(ZRoutedRpc.instance);
            ReportRpc.Register(ZRoutedRpc.instance);

            _registeredRpc = ZRoutedRpc.instance;
        }

        /// <summary>
        /// True once the webhook worker is running, i.e. messages can be queued.
        /// </summary>
        internal static bool IsActive
        {
            get { return _worker != null && _serverActive; }
        }

        /// <summary>
        /// Queues a ready-made webhook JSON payload (used for embeds such as boss deaths, and for messages that carry
        /// their own avatar such as the player join/leave notifications).
        /// </summary>
        /// <param name="webhookUrl">The Discord webhook URL to post to.</param>
        /// <param name="payloadJson">The complete JSON body.</param>
        /// <param name="label">Short description for log lines.</param>
        /// <returns>True if the payload was queued; false if the worker is not running or no webhook was given.</returns>
        internal static bool EnqueueJson(string webhookUrl, string payloadJson, string label)
        {
            WebhookWorker worker = _worker;
            if (worker == null || !_serverActive)
            {
                RelayDiagnostics.Warning("Webhook worker is not running; " + label + " was not sent.");
                return false;
            }

            return worker.EnqueueJson(webhookUrl, payloadJson, label);
        }

        private static void OnClientChat(long sender, ZPackage package)
        {
            if (!ServerConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null)
                {
                    RelayDiagnostics.Warning("Rejected chat relay from unknown peer " + sender + ".");
                    return;
                }

                int version = package.ReadInt();
                if (version != RelayProtocol.ProtocolVersion)
                {
                    RelayDiagnostics.Warning("Rejected chat relay from " + sender + ": unsupported protocol " + version + ".");
                    return;
                }

                int rawType = package.ReadInt();
                if (rawType != (int)Talker.Type.Normal && rawType != (int)Talker.Type.Shout)
                {
                    RelayDiagnostics.Warning("Rejected chat relay from " + sender + ": unsupported Talker.Type " + rawType + ".");
                    return;
                }

                string text = RelayProtocol.ReadSafeString(package, RelayProtocol.MaxTextChars);
                if (string.IsNullOrWhiteSpace(text))
                    return;

                string playerName = peer.m_playerName;
                string platformId = GetPlatformId(peer);

                QueueChat((Talker.Type)rawType, text, playerName, platformId);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to process incoming chat relay.", ex);
            }
        }

        /// <summary>
        /// Relays a chat message typed by the local player when this game IS the server (a world hosted from
        /// Valheim's own menu). There is no server peer to send an RPC to in that case, and the host is not in the
        /// peer list, so ChatSendTextPatch hands the message here directly instead.
        /// </summary>
        /// <param name="type">The chat message's talker type.</param>
        /// <param name="text">The chat message text.</param>
        internal static void RelayHostChat(Talker.Type type, string text)
        {
            if (!ServerConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                if (type != Talker.Type.Normal && type != Talker.Type.Shout)
                    return;

                if (string.IsNullOrWhiteSpace(text))
                    return;

                string playerName = null;
                if (Player.m_localPlayer != null)
                    playerName = Player.m_localPlayer.GetPlayerName();

                // The host has no network peer, so there is no platform ID to read; "host" is used for the
                // [xxxx] suffix / duplicate-name numbering.
                QueueChat(type, text, playerName, "host");
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to relay the host's chat message.", ex);
            }
        }

        /// <summary>
        /// Builds the Discord message for one chat line and queues it on the webhook worker. Shared by the
        /// RPC path (other players) and the host path (the local player on a game-hosted world).
        /// </summary>
        private static void QueueChat(Talker.Type type, string text, string playerName, string platformId)
        {
            if (text.Length > ServerConfig.MaxMessageLength.Value)
                text = text.Substring(0, ServerConfig.MaxMessageLength.Value);

            if (string.IsNullOrWhiteSpace(playerName))
                playerName = "Unknown Player";

            string characterKey = playerName + "|" + platformId;

            RelayMessage message = new RelayMessage
            {
                Type = type,
                Text = text,
                PlayerName = playerName,
                PlatformId = platformId,
                CharacterKey = characterKey
            };

            string username = BuildDiscordUsername(message);
            string content = DiscordFormatting.BuildContent(
                message,
                ServerConfig.ShoutAnsi.Value,
                ServerConfig.NormalAnsi.Value,
                ServerConfig.MaxMessageLength.Value);

            string webhookUrl = message.Type == Talker.Type.Shout
                ? GetWebhookUrl(ServerConfig.ShoutWebhookUrl.Value)
                : GetWebhookUrl(ServerConfig.NormalWebhookUrl.Value);

            if (_worker != null && _serverActive)
                _worker.Enqueue(webhookUrl, username, content);
        }

        /// <summary>
        /// Sends a server Up/Restart/Down notification synchronously so it is not lost on process exit.
        /// </summary>
        /// <param name="eventName">One of "up", "restart" or "down".</param>
        internal static void NotifyServerLifecycle(string eventName)
        {
            if (!ServerConfig.ServerNotificationsEnabled.Value || _worker == null || !_serverActive)
                return;

            string url = ServerConfig.ServerNotificationsWebhookUrl.Value;
            if (string.IsNullOrWhiteSpace(url))
                return;

            // The server name is always bold (markdown characters inside the name are escaped so they cannot break the bold).
            string boldName = BoldServerName(GetServerDisplayName());

            bool enabled;
            string message;
            if (eventName == "restart")
            {
                enabled = ServerConfig.ServerRestartNotification.Value;
                message = "Server " + boldName + " restarted.";
            }
            else if (eventName == "down")
            {
                enabled = ServerConfig.ServerDownNotification.Value;
                message = "Server " + boldName + " is going down.";
            }
            else
            {
                enabled = ServerConfig.ServerUpNotification.Value;
                message = "Server " + boldName + " is up.";
            }

            if (!enabled)
                return;

            // null username = Discord uses the name set on the webhook itself.
            string username = ServerConfig.UseDiscordWebhookName.Value
                ? null
                : ServerConfig.ServerNotificationsWebhookName;

            _worker.SendNow(url, username, message, LifecycleSendTimeoutMs, "server notification (" + eventName + ")");
        }

        /// <summary>
        /// The server name as bold Discord markdown, for use inside a message.
        /// </summary>
        internal static string BoldServerName(string serverName)
        {
            return "**" + BossCatalog.EscapeDiscordMarkdown(serverName) + "**";
        }

        /// <summary>
        /// Reads the dedicated server's configured display name via reflection.
        /// </summary>
        /// <returns>The server's display name, or a generic fallback if unavailable.</returns>
        internal static string GetServerDisplayName()
        {
            try
            {
                FieldInfo field = typeof(ZNet).GetField(
                    "m_ServerName",
                    BindingFlags.Static | BindingFlags.NonPublic);

                string name = field != null ? field.GetValue(null) as string : null;
                if (!string.IsNullOrWhiteSpace(name))
                    return SanitizeUsername(name.Trim());
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not read server name: " + ex.Message);
            }

            return "Valheim Server";
        }

        /// <summary>
        /// Consumes the clean-shutdown marker and reports whether the current startup is a restart, i.e.
        /// the marker exists AND the shutdown it records happened within the configured restart window.
        /// An older marker (server stayed off for a while) counts as a normal startup.
        /// </summary>
        /// <returns>True if this startup should be announced as a restart.</returns>
        internal static bool ConsumeCleanShutdownMarker()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_restartMarkerPath) || !File.Exists(_restartMarkerPath))
                    return false;

                string text = null;
                try
                {
                    text = File.ReadAllText(_restartMarkerPath);
                }
                finally
                {
                    File.Delete(_restartMarkerPath);
                }

                DateTime shutdownUtc;
                if (!DateTime.TryParse(
                        text == null ? null : text.Trim(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out shutdownUtc))
                {
                    RelayDiagnostics.Warning("Clean-shutdown marker had no readable timestamp; reporting a normal startup.");
                    return false;
                }

                double downSeconds = (DateTime.UtcNow - shutdownUtc).TotalSeconds;
                int windowSeconds = ServerConfig.ServerRestartWindowSeconds.Value;
                bool isRestart = downSeconds >= 0 && downSeconds <= windowSeconds;

                RelayDiagnostics.Info(
                    "Server startup " + downSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                    "s after last clean shutdown (restart window " + windowSeconds + "s): " +
                    (isRestart ? "restart" : "normal startup") + ".");

                return isRestart;
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not inspect clean-shutdown marker: " + ex.Message);
                return false;
            }
        }

        internal static void WriteCleanShutdownMarker()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_restartMarkerPath))
                    File.WriteAllText(_restartMarkerPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Warning("Could not write clean-shutdown marker: " + ex.Message);
            }
        }

        private static string GetPlatformId(ZNetPeer peer)
        {
            try
            {
                if (peer != null && peer.m_socket != null)
                {
                    string hostName = peer.m_socket.GetHostName();
                    if (!string.IsNullOrWhiteSpace(hostName))
                        return hostName.Trim();
                }
            }
            catch
            {
            }

            return peer != null ? peer.m_uid.ToString() : "unknown";
        }

        private static string GetWebhookUrl(string specificWebhook)
        {
            if (!string.IsNullOrWhiteSpace(specificWebhook))
                return specificWebhook.Trim();

            return ServerConfig.WebhookUrl.Value != null ? ServerConfig.WebhookUrl.Value.Trim() : string.Empty;
        }

        private static string BuildDiscordUsername(RelayMessage message)
        {
            string name = SanitizeUsername(message.PlayerName);

            switch (ServerConfig.NameDisplay.Value)
            {
                case NameDisplayMode.NameOnly:
                    return LimitUsername(name);

                case NameDisplayMode.NameWithIdSuffix:
                    string id = message.PlatformId ?? "unknown";
                    string suffix = id.Length <= 4 ? id : id.Substring(id.Length - 4);
                    return LimitUsername(name + " [" + suffix + "]");

                case NameDisplayMode.NameWithNumber:
                    return LimitUsername(name + " [" + GetDuplicateNameNumber(message) + "]");

                default:
                    return LimitUsername(name);
            }
        }

        /// <summary>
        /// Computes a deterministic per-session duplicate-name suffix for NameWithNumber display.
        /// </summary>
        /// <param name="message">The relay message whose sender's duplicate number is needed.</param>
        /// <returns>A 1-based number, stable for the current server session.</returns>
        private static int GetDuplicateNameNumber(RelayMessage message)
        {
            string targetName = message.PlayerName ?? string.Empty;
            List<string> ids = new List<string>();

            try
            {
                if (ZNet.instance != null)
                {
                    foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    {
                        if (peer == null || peer.m_playerName != targetName)
                            continue;

                        string id = GetPlatformId(peer);
                        if (!ids.Contains(id))
                            ids.Add(id);
                    }
                }
            }
            catch
            {
            }

            if (!ids.Contains(message.PlatformId ?? string.Empty))
                ids.Add(message.PlatformId ?? string.Empty);

            ids.Sort(StringComparer.Ordinal);

            int index = ids.IndexOf(message.PlatformId ?? string.Empty);
            return index >= 0 ? index + 1 : 1;
        }

        internal static string SanitizeUsername(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown Player";

            string s = value.Replace("\r", " ").Replace("\n", " ");
            s = s.Replace("@everyone", "@\u200beveryone")
                 .Replace("@here", "@\u200bhere");
            return s.Trim();
        }

        internal static string LimitUsername(string value)
        {
            if (value.Length <= 80)
                return value;
            return value.Substring(0, 79) + "…";
        }
    }
}
