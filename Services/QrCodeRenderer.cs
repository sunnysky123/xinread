using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace xinread.Services;

/// <summary>把二维码内容渲染成 WinUI 可用的位图。</summary>
public static class QrCodeRenderer
{
    public static async Task<BitmapImage> RenderAsync(string content, int pixelsPerModule = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        using var png = new PngByteQRCode(data);
        byte[] bytes = png.GetGraphic(pixelsPerModule);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
