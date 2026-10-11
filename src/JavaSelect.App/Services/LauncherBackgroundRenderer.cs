using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace JavaSelect.App.Services;

internal static class LauncherBackgroundRenderer
{
    internal static async Task<ImageSource> LoadAsync(string path, int width, int height, double blurRadius, CancellationToken token)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8_000_000)
            throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            using var stream = await file.OpenReadAsync().AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192
                || (long)decoder.PixelWidth * decoder.PixelHeight > 40_000_000)
                throw new InvalidDataException("Unsupported background image dimensions.");

            var scale = Math.Min(1, Math.Max(width / (double)decoder.OrientedPixelWidth, height / (double)decoder.OrientedPixelHeight));
            scale = Math.Min(scale, 4096d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var scaledWidth = (uint)Math.Max(1, Math.Ceiling(decoder.PixelWidth * scale));
            var scaledHeight = (uint)Math.Max(1, Math.Ceiling(decoder.PixelHeight * scale));
            var rotated = decoder.PixelWidth != decoder.OrientedPixelWidth;
            var orientedWidth = rotated ? scaledHeight : scaledWidth;
            var orientedHeight = rotated ? scaledWidth : scaledHeight;
            var cropWidth = Math.Min(orientedWidth, (uint)Math.Max(1, Math.Round(orientedHeight * width / (double)height)));
            var cropHeight = Math.Min(orientedHeight, (uint)Math.Max(1, Math.Round(orientedWidth * height / (double)width)));
            // WIC scales in source coordinates, then applies EXIF orientation before cropping.
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth, ScaledHeight = scaledHeight, InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = (orientedWidth - cropWidth) / 2, Y = (orientedHeight - cropHeight) / 2, Width = cropWidth, Height = cropHeight }
            };
            var decoded = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token)).DetachPixelData();
            token.ThrowIfCancellationRequested();
            var resized = LauncherBackgroundPixels.Resize(decoded, (int)cropWidth, (int)cropHeight, width, height, token);
            LauncherBackgroundPixels.Blur(resized, width, height, blurRadius, token);
            return resized;
        }, token);

        token.ThrowIfCancellationRequested();
        var bitmap = new WriteableBitmap(width, height);
        using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }
}
