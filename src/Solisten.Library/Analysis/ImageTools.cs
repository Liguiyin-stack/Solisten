using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Solisten.Library.Analysis;

/// <summary>Windows.Graphics.Imaging 解码与缩放。</summary>
public static class ImageTools
{
    /// <summary>解码并缩放为 size×size 的 straight-alpha RGBA8 像素（不保持纵横比，只用于取色）。</summary>
    public static async Task<byte[]> DecodeRgbaAsync(byte[] data, int size)
    {
        using var stream = await ToStreamAsync(data);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)size,
            ScaledHeight = (uint)size,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return pixels.DetachPixelData();
    }

    /// <summary>生成边长不超过 maxSize 的 PNG 缩略图（保持纵横比）。</summary>
    public static async Task WriteThumbnailAsync(byte[] data, int maxSize, string path)
    {
        using var input = await ToStreamAsync(data);
        var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1.0, maxSize / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint w = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
        uint h = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, w, h, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    private static async Task<IRandomAccessStream> ToStreamAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        return stream;
    }
}

internal static class AtomicFile
{
    /// <summary>先写临时文件再替换，避免缓存中留下半个文件。内容寻址的缓存文件被并发写入时，先到者胜出。</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var data = bytes.ToArray();
        Write(path, s => s.Write(data));
    }

    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = File.Create(tmp)) write(fs);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
            // 另一个线程刚刚写入了同一个文件。
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
