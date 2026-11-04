using System;
using System.Reflection;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// <see cref="ImageConversion.LoadImage(Texture2D, byte[], bool)"/> without the compiler looking at its ReadOnlySpan sibling.
/// The mod is built for net48 (shared Kydl.Mod.props), which has no ReadOnlySpan, while the game's Unity 6 DLL has a second overload with
/// it; with LangVersion latest the compiler stops with CS0518. The array overload is therefore called by cached reflection
/// (Claude\MISTAKES.md standing lesson 11).
/// </summary>
internal static class ImageConversionCompat
{
    private static MethodInfo _loadImage;

    /// <summary>
    /// Decodes a PNG / JPG byte array into <paramref name="texture"/>. Returns false when the data could not be decoded
    /// or the overload does not exist in this game version (the reason is thrown as an exception only in the latter case).
    /// </summary>
    internal static bool LoadImage(Texture2D texture, byte[] data, bool markNonReadable)
    {
        if (_loadImage == null)
        {
            _loadImage = typeof(ImageConversion).GetMethod(
                "LoadImage",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
                null);

            if (_loadImage == null)
                throw new MissingMethodException("UnityEngine.ImageConversion.LoadImage(Texture2D, byte[], bool) was not found in this game version");
        }

        return (bool)_loadImage.Invoke(null, new object[] { texture, data, markNonReadable });
    }
}
