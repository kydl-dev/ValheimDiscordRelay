using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimDiscordRelay.Report;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Watches who is connected to this server and posts the join / leave messages to the
    /// [Player Notifications] webhook.
    ///
    /// Why this is based on connections and not on Valheim's own chat message: the game sends "I have arrived!" every
    /// time a character spawns (after logging in AND after every respawn) and sends nothing at all when somebody leaves.
    /// Watching the connected peers gives exactly one join per connection and one leave per disconnect.
    /// Valheim's own arrival shout is still intercepted on the client (see ChatSendTextPatch) so it never reaches Discord
    /// while this feature is on.
    ///
    /// Everything here runs on the main thread, from ServerPlugin.Update().
    /// </summary>
    internal static class PlayerNotifications
    {
        private sealed class OnlinePlayer
        {
            public long Uid;
            public string Name;
        }

        private const float PollIntervalSeconds = 1f;
        private const int MaxNameChars = 64;

        private static readonly Dictionary<long, OnlinePlayer> Online = new Dictionary<long, OnlinePlayer>();

        /// <summary>The ZNet the Online table belongs to. A new ZNet (new world session) starts from an empty table.</summary>
        private static ZNet _znet;

        /// <summary>Set while the server / hosted world is being shut down, so everybody vanishing is not announced as leaving.</summary>
        private static bool _shuttingDown;

        /// <summary>True once the host of a game-hosted world (who is not a network peer) has spawned.</summary>
        private static bool _hostOnline;
        private static string _hostName;

        private static float _nextPollTime;
        private static bool _warnedNoWebhook;

        // ---------------------------------------------------------------- called by the plugin / patches

        /// <summary>
        /// Polls the connected peers about once per second and announces joins and leaves.
        /// </summary>
        internal static void Tick()
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null || !znet.IsServer())
                {
                    Reset();
                    return;
                }

                float now = Time.unscaledTime;
                if (now < _nextPollTime)
                    return;

                _nextPollTime = now + PollIntervalSeconds;

                if (!ReferenceEquals(znet, _znet))
                {
                    Reset();
                    _znet = znet;
                }

                if (_shuttingDown)
                    return;

                Poll(znet);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[PlayerNotifications] Presence update failed.", ex);
            }
        }

        /// <summary>
        /// Called the moment the game drops a connected peer (ZNet.Disconnect): a player choosing Log Out, quitting to
        /// desktop, losing the connection or being kicked. Announces the leave straight away instead of waiting for the
        /// next poll; the poll in Tick() stays as a fallback and cannot announce the same player twice, because the
        /// player is removed from the Online table here first.
        /// </summary>
        /// <param name="peer">The peer that is being disconnected.</param>
        internal static void OnPeerDisconnected(ZNetPeer peer)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (peer == null || znet == null || !znet.IsServer())
                    return;

                // Everybody vanishing because the world is closing is not a wave of players leaving.
                if (_shuttingDown || !ReferenceEquals(znet, _znet))
                    return;

                OnlinePlayer player;
                if (!Online.TryGetValue(peer.m_uid, out player))
                    return;

                Online.Remove(peer.m_uid);
                AnnounceLeave(player.Name);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("[PlayerNotifications] Disconnect handling failed.", ex);
            }
        }

        /// <summary>
        /// Called when the server (or a hosted world) starts shutting down: from now on peers disappearing is not a "leave".
        /// </summary>
        internal static void MarkShuttingDown()
        {
            _shuttingDown = true;
        }

        /// <summary>
        /// Names of everybody currently online (peers, plus the host of a game-hosted world).
        /// </summary>
        internal static List<string> GetOnlineNames()
        {
            List<string> names = new List<string>(Online.Count + 1);

            foreach (OnlinePlayer player in Online.Values)
                names.Add(player.Name);

            if (_hostOnline && _hostName != null)
                names.Add(_hostName);

            return names;
        }

        /// <summary>
        /// Number of players currently online, including the host of a game-hosted world.
        /// </summary>
        internal static int Population
        {
            get { return Online.Count + (_hostOnline ? 1 : 0); }
        }

        // ---------------------------------------------------------------- message text (the exact wording lives here)

        /// <summary>
        /// "{Player.name} has joined the world. Population: {players.count}. Good luck!"
        /// </summary>
        internal static string BuildJoinMessage(string playerName, int population)
        {
            return BossCatalog.EscapeDiscordMarkdown(playerName) +
                   " has joined the world. Population: " +
                   population.ToString(CultureInfo.InvariantCulture) +
                   ". Good luck!";
        }

        /// <summary>
        /// "{Player.name} had to leave us. Hope they had fun. Population: {players.count}."
        /// </summary>
        internal static string BuildLeaveMessage(string playerName, int population)
        {
            return BossCatalog.EscapeDiscordMarkdown(playerName) +
                   " had to leave us. Hope they had fun. Population: " +
                   population.ToString(CultureInfo.InvariantCulture) +
                   ".";
        }

        // ---------------------------------------------------------------- internals

        private static void Reset()
        {
            if (Online.Count > 0)
                Online.Clear();

            _znet = null;
            _shuttingDown = false;
            _hostOnline = false;
            _hostName = null;
        }

        private static void Poll(ZNet znet)
        {
            HashSet<long> connected = new HashSet<long>();
            List<OnlinePlayer> arrived = new List<OnlinePlayer>();

            foreach (ZNetPeer peer in znet.GetPeers())
            {
                // A peer that is still handshaking has no uid / name yet; it is picked up on a later poll.
                if (peer == null || peer.m_uid == 0L)
                    continue;

                string name = CleanName(peer.m_playerName);
                if (name == null)
                    continue;

                connected.Add(peer.m_uid);

                if (!Online.ContainsKey(peer.m_uid))
                    arrived.Add(new OnlinePlayer { Uid = peer.m_uid, Name = name });
            }

            List<OnlinePlayer> departed = null;
            foreach (KeyValuePair<long, OnlinePlayer> pair in Online)
            {
                if (connected.Contains(pair.Key))
                    continue;

                if (departed == null)
                    departed = new List<OnlinePlayer>();

                departed.Add(pair.Value);
            }

            // Leaves first, so the population shown in each message follows the real order of events.
            if (departed != null)
            {
                foreach (OnlinePlayer player in departed)
                {
                    Online.Remove(player.Uid);
                    AnnounceLeave(player.Name);
                }
            }

            foreach (OnlinePlayer player in arrived)
            {
                Online[player.Uid] = player;
                AnnounceJoin(player.Name);
            }

            // A world hosted from Valheim's own menu: the host is not a network peer, so they are noticed through
            // their own character instead. They count towards the population but their "leave" is the server going down.
            if (!_hostOnline && !znet.IsDedicated() && Player.m_localPlayer != null)
            {
                string hostName = CleanName(Player.m_localPlayer.GetPlayerName());
                if (hostName != null)
                {
                    _hostOnline = true;
                    _hostName = hostName;
                    AnnounceJoin(hostName);
                }
            }
        }

        private static void AnnounceJoin(string name)
        {
            int population = Population;

            RelayDiagnostics.Info("[PlayerNotifications] '" + name + "' joined. Population: " + population + ".");

            // Counted for the weekly report whether or not the Discord messages are enabled.
            ReportStats.RecordJoin(name);

            Send(BuildJoinMessage(name, population), true, "player joined (" + name + ")");
        }

        private static void AnnounceLeave(string name)
        {
            int population = Population;

            RelayDiagnostics.Info("[PlayerNotifications] '" + name + "' left. Population: " + population + ".");

            Send(BuildLeaveMessage(name, population), false, "player left (" + name + ")");
        }

        private static void Send(string content, bool arrival, string label)
        {
            if (!PlayerNotificationConfig.Enabled.Value)
                return;

            string url = PlayerNotificationConfig.WebhookUrl.Value == null
                ? string.Empty
                : PlayerNotificationConfig.WebhookUrl.Value.Trim();

            if (url.Length == 0)
            {
                if (!_warnedNoWebhook)
                {
                    _warnedNoWebhook = true;
                    RelayDiagnostics.Warning("[PlayerNotifications] Enabled, but [Player Notifications] Server - Webhook URL is empty; join/leave messages are not sent.");
                }

                return;
            }

            _warnedNoWebhook = false;

            string json = DiscordFormatting.BuildWebhookJson(
                PlayerNotificationConfig.WebhookName,
                content,
                PlayerNotificationConfig.GetAvatarUrl(arrival));

            ServerRelay.EnqueueJson(url, json, label);
        }

        private static string CleanName(string raw)
        {
            string name = BossCatalog.StripTags(raw);
            if (name.Length == 0)
                return null;

            if (name.Length > MaxNameChars)
                name = name.Substring(0, MaxNameChars);

            return name;
        }
    }
}
