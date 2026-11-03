using System;
using System.Text;

namespace ValheimDiscordRelay
{
    internal static class RelayProtocol
    {
        public const string RpcName = "ValheimDiscordRelay.Chat.v1";
        public const string BossDamageRpcName = "ValheimDiscordRelay.BossDamage.v1";
        public const string BossDeathRpcName = "ValheimDiscordRelay.BossDeath.v1";

        // Client-to-client (routed through the server): the boss owner asks the last attacker to record the boss video,
        // and the attacker answers. See Client/BossVideoRpc.cs.
        public const string BossVideoRequestRpcName = "ValheimDiscordRelay.BossVideoRequest.v1";
        public const string BossVideoReplyRpcName = "ValheimDiscordRelay.BossVideoReply.v1";
        public const int ProtocolVersion = 1;
        public const int MaxTextChars = 1000;

        public static bool IsRelayedType(Talker.Type type)
        {
            return type == Talker.Type.Normal || type == Talker.Type.Shout;
        }

        public static string ReadSafeString(ZPackage package, int maxChars)
        {
            if (package == null)
                throw new ArgumentNullException("package");

            string value = package.ReadString() ?? string.Empty;
            if (value.Length > maxChars)
                value = value.Substring(0, maxChars);
            return value;
        }
    }

    internal sealed class RelayMessage
    {
        public Talker.Type Type;
        public string Text;
        public string PlayerName;
        public string PlatformId;
        public string CharacterKey;
    }

    internal static class DiscordFormatting
    {
        public static string BuildContent(RelayMessage message, string shoutAnsi, string normalAnsi, int maxContentChars)
        {
            string ansi = message.Type == Talker.Type.Shout ? shoutAnsi : normalAnsi;
            if (string.IsNullOrEmpty(ansi))
                ansi = "\u001b[2;36m";

            string text = message.Text ?? string.Empty;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // A Discord triple-backtick sequence would terminate the ANSI code block.
            text = text.Replace("```", "`\u200b``");

            int overhead = 16 + ansi.Length + 4;
            int allowed = Math.Max(1, maxContentChars - overhead);
            if (text.Length > allowed)
                text = text.Substring(0, allowed - 1) + "…";

            return "```ansi\n" + ansi + text + "\u001b[0m\n```";
        }

        public static string JsonEscape(string value)
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

        public static string BuildWebhookJson(string username, string content)
        {
            return BuildWebhookJson(username, content, null);
        }

        /// <summary>
        /// Builds a plain-text webhook payload. An empty/null username is omitted, so Discord shows the name set on the
        /// webhook itself; an empty/null avatar URL is omitted, so Discord shows the webhook's own icon.
        /// </summary>
        public static string BuildWebhookJson(string username, string content, string avatarUrl)
        {
            StringBuilder sb = new StringBuilder((content == null ? 0 : content.Length) + 128);

            sb.Append('{');

            if (!string.IsNullOrWhiteSpace(username))
                sb.Append("\"username\":\"").Append(JsonEscape(username)).Append("\",");

            if (!string.IsNullOrWhiteSpace(avatarUrl))
                sb.Append("\"avatar_url\":\"").Append(JsonEscape(avatarUrl)).Append("\",");

            sb.Append("\"content\":\"").Append(JsonEscape(content)).Append("\",\"allowed_mentions\":{\"parse\":[]}}");
            return sb.ToString();
        }

        /// <summary>
        /// Builds a webhook payload carrying a single embed. Empty avatar/thumbnail/image URLs are omitted.
        /// </summary>
        public static string BuildEmbedWebhookJson(
            string username,
            string avatarUrl,
            string title,
            string description,
            string thumbnailUrl,
            string imageUrl)
        {
            StringBuilder sb = new StringBuilder(512);

            sb.Append("{\"username\":\"").Append(JsonEscape(username)).Append('"');

            if (!string.IsNullOrEmpty(avatarUrl))
                sb.Append(",\"avatar_url\":\"").Append(JsonEscape(avatarUrl)).Append('"');

            sb.Append(",\"allowed_mentions\":{\"parse\":[]},\"embeds\":[{");
            sb.Append("\"title\":\"").Append(JsonEscape(title)).Append('"');
            sb.Append(",\"description\":\"").Append(JsonEscape(description)).Append('"');

            if (!string.IsNullOrEmpty(thumbnailUrl))
                sb.Append(",\"thumbnail\":{\"url\":\"").Append(JsonEscape(thumbnailUrl)).Append("\"}");

            if (!string.IsNullOrEmpty(imageUrl))
                sb.Append(",\"image\":{\"url\":\"").Append(JsonEscape(imageUrl)).Append("\"}");

            sb.Append("}]}");
            return sb.ToString();
        }
    }
}
