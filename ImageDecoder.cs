using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinOcrApi;

/// <summary>把图片字节解码成两个引擎都能吃的 BGRA8 位图。</summary>
internal static class ImageDecoder
{
    /// <param name="maxDim">最长边上限，超过会自动等比缩小；0 表示不限制。</param>
    public static async Task<SoftwareBitmap> DecodeAsync(
        byte[] bytes, double scale, int rotate, int maxDim, CancellationToken ct)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);

        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OcrImageException($"无法解码图片（支持 png/jpg/bmp/tiff/gif/heic）：{ex.Message}");
        }

        var srcWidth = decoder.PixelWidth;
        var srcHeight = decoder.PixelHeight;
        if (srcWidth == 0 || srcHeight == 0)
        {
            throw new OcrImageException("图片尺寸为 0，无法识别。");
        }

        var effectiveScale = scale;
        if (maxDim > 0 && Math.Max(srcWidth, srcHeight) * effectiveScale > maxDim)
        {
            effectiveScale = maxDim / (double)Math.Max(srcWidth, srcHeight);
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(srcWidth * effectiveScale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(srcHeight * effectiveScale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
            Rotation = rotate switch
            {
                90 => BitmapRotation.Clockwise90Degrees,
                180 => BitmapRotation.Clockwise180Degrees,
                270 => BitmapRotation.Clockwise270Degrees,
                _ => BitmapRotation.None,
            },
        };

        try
        {
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OcrImageException($"图片转换失败：{ex.Message}");
        }
    }
}
