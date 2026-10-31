using System;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Small, generic text helpers shared by chat relay, screenshots and death videos.
    /// </summary>
    internal static class ClientTextUtils
    {
        /// <summary>
        /// True while the player is typing into the game: the chat box has focus, the console is open, or a text-entry
        /// dialog (a sign, a map pin name) is shown. Hotkeys must not fire then, or typing the key would trigger them.
        /// </summary>
        internal static bool IsTypingInGame()
        {
            try
            {
                if (Chat.instance != null && Chat.instance.HasFocus())
                    return true;

                if (global::Console.IsVisible())
                    return true;

                if (TextInput.IsVisible())
                    return true;
            }
            catch
            {
                // If the game cannot say, do not block the hotkey.
            }

            return false;
        }

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

        /// <summary>
        /// Turns text such as a player name into something that is safe inside a file name: every character a file name
        /// cannot hold (\ / : * ? " &lt; &gt; | and control characters) becomes "_", and the result is cut to
        /// <paramref name="maxChars"/>, trimmed, and never ends in a dot. Never throws.
        /// </summary>
        /// <param name="value">The text to clean; may be null or empty.</param>
        /// <param name="fallback">Returned when nothing usable is left (empty text, or only characters that were replaced).</param>
        /// <param name="maxChars">The most characters to keep.</param>
        internal static string SafeFileNamePart(string value, string fallback, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            value = value.Trim();

            // The platform's list, plus the Windows set on every platform (a game run through Proton/Wine uses Windows rules).
            char[] invalid = Path.GetInvalidFileNameChars();
            const string windowsInvalid = "\\/:*?\"<>|";

            StringBuilder sb = new StringBuilder(Math.Min(value.Length, Math.Max(1, maxChars)));
            foreach (char c in value)
            {
                if (sb.Length >= maxChars)
                    break;

                bool bad = char.IsControl(c) || windowsInvalid.IndexOf(c) >= 0 || Array.IndexOf(invalid, c) >= 0;
                sb.Append(bad ? '_' : c);
            }

            // Do not leave half of a surrogate pair (an emoji, say) at the cut.
            if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1]))
                sb.Length--;

            string result = sb.ToString().Trim().TrimEnd('.', ' ');
            return result.Trim('_').Length == 0 ? fallback : result;
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
