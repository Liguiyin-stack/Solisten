using System.Security.Cryptography;

namespace Solisten.Library.Metadata;

/// <summary>§6.1 ③：内嵌图片 → 同目录 cover/folder/front.(jpg|png) → 无封面。</summary>
public static class CoverLocator
{
    private static readonly string[] Names = ["cover", "folder", "front"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    public static string? FindFolderCover(string directory)
    {
        foreach (var name in Names)
        {
            foreach (var ext in Extensions)
            {
                var candidate = Path.Combine(directory, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data))[..20];

    public static string ExtensionFor(string mime, ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) return ".png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])) return ".jpg";
        return mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
    }
}
