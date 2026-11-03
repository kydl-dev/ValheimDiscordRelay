using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimDiscordRelay.Client
{
    /// <summary>
    /// Joins several animated WebP files (the parallel encoder chunks) into one animated WebP by concatenating their
    /// frame chunks. Every ANMF chunk of an animated WebP is self-contained (position, size, duration, blend and
    /// dispose flags, and its own image data), so the frames can be moved between files without being decoded or
    /// re-encoded; only the container header (VP8X canvas, ANIM loop settings and the RIFF size) is rebuilt.
    /// </summary>
    internal static class WebPAnimationMerger
    {
        private const byte AnimationFlag = 0x02;

        internal static bool TryMerge(IList<string> parts, string output, out string error)
        {
            error = null;

            if (parts == null || parts.Count == 0)
            {
                error = "no parts were supplied";
                return false;
            }

            try
            {
                byte[] anim = null;
                byte flags = 0;
                int canvasWidth = 0;
                int canvasHeight = 0;
                List<byte[]> frameChunks = new List<byte[]>();

                for (int p = 0; p < parts.Count; p++)
                {
                    byte[] data = File.ReadAllBytes(parts[p]);

                    if (data.Length < 12 ||
                        data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
                        data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
                    {
                        error = "part " + p + " is not a RIFF/WebP file";
                        return false;
                    }

                    long end = Math.Min((long)ReadUInt32(data, 4) + 8L, data.Length);
                    long pos = 12;
                    bool sawHeader = false;
                    int framesInPart = 0;

                    while (pos + 8 <= end)
                    {
                        long size = ReadUInt32(data, (int)pos + 4);
                        long payloadStart = pos + 8;
                        long payloadEnd = payloadStart + size;

                        if (payloadEnd > end)
                        {
                            error = "part " + p + " has a truncated chunk";
                            return false;
                        }

                        string id = new string(new[]
                        {
                            (char)data[pos], (char)data[pos + 1], (char)data[pos + 2], (char)data[pos + 3]
                        });

                        if (id == "VP8X")
                        {
                            if (size < 10)
                            {
                                error = "part " + p + " has a malformed VP8X chunk";
                                return false;
                            }

                            byte partFlags = data[payloadStart];
                            if ((partFlags & AnimationFlag) == 0)
                            {
                                error = "part " + p + " is not an animation";
                                return false;
                            }

                            int width = 1 + ReadUInt24(data, (int)payloadStart + 4);
                            int height = 1 + ReadUInt24(data, (int)payloadStart + 7);

                            if (p == 0)
                            {
                                canvasWidth = width;
                                canvasHeight = height;
                            }
                            else if (width != canvasWidth || height != canvasHeight)
                            {
                                error = "part " + p + " has a different canvas size (" + width + "x" + height +
                                        " instead of " + canvasWidth + "x" + canvasHeight + ")";
                                return false;
                            }

                            flags |= partFlags;
                            sawHeader = true;
                        }
                        else if (id == "ANIM")
                        {
                            // Loop count and background colour come from the first part.
                            if (p == 0 && size >= 6)
                            {
                                anim = new byte[6];
                                Buffer.BlockCopy(data, (int)payloadStart, anim, 0, 6);
                            }
                        }
                        else if (id == "ANMF")
                        {
                            // Header + payload + the pad byte RIFF adds after an odd-sized chunk.
                            int length = (int)(8 + size);
                            byte[] chunk = new byte[length + (int)(size & 1)];
                            Buffer.BlockCopy(data, (int)pos, chunk, 0, length);
                            frameChunks.Add(chunk);
                            framesInPart++;
                        }

                        pos = payloadEnd + (size & 1);
                    }

                    if (!sawHeader)
                    {
                        error = "part " + p + " has no VP8X header";
                        return false;
                    }

                    if (framesInPart == 0)
                    {
                        error = "part " + p + " contains no frames";
                        return false;
                    }
                }

                if (anim == null)
                {
                    error = "the first part has no ANIM chunk";
                    return false;
                }

                long riffPayload = 4 + (8 + 10) + (8 + 6);
                foreach (byte[] chunk in frameChunks)
                    riffPayload += chunk.Length;

                if (riffPayload > uint.MaxValue - 8)
                {
                    error = "the joined animation would exceed the WebP size limit";
                    return false;
                }

                using (FileStream stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }, 0, 4);
                    WriteUInt32(stream, (uint)riffPayload);
                    stream.Write(new byte[] { (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, 0, 4);

                    byte[] vp8x = new byte[10];
                    vp8x[0] = (byte)(flags | AnimationFlag);
                    WriteUInt24(vp8x, 4, canvasWidth - 1);
                    WriteUInt24(vp8x, 7, canvasHeight - 1);
                    stream.Write(new byte[] { (byte)'V', (byte)'P', (byte)'8', (byte)'X' }, 0, 4);
                    WriteUInt32(stream, 10);
                    stream.Write(vp8x, 0, vp8x.Length);

                    stream.Write(new byte[] { (byte)'A', (byte)'N', (byte)'I', (byte)'M' }, 0, 4);
                    WriteUInt32(stream, 6);
                    stream.Write(anim, 0, anim.Length);

                    foreach (byte[] chunk in frameChunks)
                        stream.Write(chunk, 0, chunk.Length);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                try
                {
                    if (File.Exists(output))
                        File.Delete(output);
                }
                catch
                {
                }

                return false;
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static int ReadUInt24(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static void WriteUInt24(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        }
    }
}
