using QRCoder;

namespace Solisten.Integrations.NetEase.Api;

/// <summary>在本机生成二维码 PNG（不经过任何在线服务）。</summary>
internal static class QrImage
{
    public static byte[] Png(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }
}
