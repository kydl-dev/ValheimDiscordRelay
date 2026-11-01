using System;
using System.Collections.Generic;

namespace ValheimDiscordRelay.Server
{
    internal static class RelayUsernames
    {
        internal static string BuildDiscordUsername(RelayMessage message)
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

                        string id = ServerRelay.GetPlatformId(peer);
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
