using System.Text;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Small, generic text helpers shared by chat relay, screenshots and death videos.
    /// </summary>
    internal static class ClientTextUtils
    {
        internal static string GetLocalPlayerName()
        {
            if (Player.m_localPlayer == null)
                return "Valheim Player";

            try
            {
                string value = Player.m_localPlayer.GetPlayerName();
                if (!string.IsNullOrWhiteSpace(value))
                    return SanitizeUsername(value);
            }
            catch { }

            return "Valheim Player";
        }

        internal static string SanitizeUsername(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Valheim Player";

            string s = value.Replace("\r", " ").Replace("\n", " ");
            s = s.Replace("@everyone", "@\u200beveryone")
                 .Replace("@here", "@\u200bhere")
                 .Trim();
            if (s.Length > 80)
                s = s.Substring(0, 79) + "…";
            return s;
        }

        internal static string JsonEscape(string value)
        {
            if (value == null)
                return string.Empty;

            StringBuilder sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
