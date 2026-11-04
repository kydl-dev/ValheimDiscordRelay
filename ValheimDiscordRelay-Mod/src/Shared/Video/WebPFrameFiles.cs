using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValheimDiscordRelay.Client;

internal static class WebPFrameFiles
{
    internal static void ValidateFrames(List<string> frames)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            string frame = frames[i];

            if (string.IsNullOrWhiteSpace(frame))
                throw new InvalidDataException("Frame " + i + " has an empty path.");

            if (!File.Exists(frame))
                throw new FileNotFoundException("Frame " + i + " does not exist.", frame);

            FileInfo info = new FileInfo(frame);
            if (info.Length <= 0)
                throw new InvalidDataException("Frame " + i + " is empty: " + frame);

            if (!IsSupportedImageFile(frame))
                throw new InvalidDataException("Frame " + i + " has an unsupported image extension: " + frame);

            if (!HasValidImageSignature(frame))
                throw new InvalidDataException("Frame " + i + " does not have a valid JPEG/PNG signature: " + frame);
        }
    }

    private static bool IsSupportedImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidImageSignature(string path)
    {
        byte[] header = new byte[8];
        using (FileStream stream = File.OpenRead(path))
        {
            int total = 0;
            while (total < header.Length)
            {
                int read = stream.Read(header, total, header.Length - total);
                if (read <= 0)
                    return false;
                total += read;
            }
        }

        bool png = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E &&
                   header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A &&
                   header[6] == 0x1A && header[7] == 0x0A;

        bool jpg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        return png || jpg;
    }

    internal static string QuoteArgument(string value)
    {
        if (value == null)
            return "\"\"";

        StringBuilder result = new StringBuilder();
        result.Append('"');
        int backslashes = 0;

        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(c);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
