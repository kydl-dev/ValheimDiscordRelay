using System.IO;

namespace ValheimDiscordRelay.Client
{
    internal static class EmbeddedResource
    {
        internal static byte[] Read(string name)
        {
            try
            {
                var assembly = typeof(EmbeddedResource).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null)
                        return null;

                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
