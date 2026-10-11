namespace JavaSelect.App.Services;

internal static class LauncherBackgroundPixels
{
    internal static byte[] Resize(byte[] pixels, int sourceWidth, int sourceHeight, int width, int height, CancellationToken token)
    {
        if (pixels.Length != checked(sourceWidth * sourceHeight * 4)) throw new InvalidDataException("Unexpected background pixel count.");
        if (sourceWidth == width && sourceHeight == height) return pixels;
        var result = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var sy = Math.Clamp((y + .5) * sourceHeight / height - .5, 0, sourceHeight - 1);
            var y0 = (int)sy; var y1 = Math.Min(y0 + 1, sourceHeight - 1); var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * sourceWidth / width - .5, 0, sourceWidth - 1);
                var x0 = (int)sx; var x1 = Math.Min(x0 + 1, sourceWidth - 1); var fx = sx - x0;
                for (var channel = 0; channel < 4; channel++)
                {
                    var top = pixels[(y0 * sourceWidth + x0) * 4 + channel] * (1 - fx) + pixels[(y0 * sourceWidth + x1) * 4 + channel] * fx;
                    var bottom = pixels[(y1 * sourceWidth + x0) * 4 + channel] * (1 - fx) + pixels[(y1 * sourceWidth + x1) * 4 + channel] * fx;
                    result[(y * width + x) * 4 + channel] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                }
            }
        }
        return result;
    }

    internal static void Blur(byte[] pixels, int width, int height, double sigma, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(sigma) || sigma < 0 || sigma > 120) throw new ArgumentOutOfRangeException(nameof(sigma));
        if (pixels.Length != checked(width * height * 4)) throw new InvalidDataException("Unexpected background pixel count.");
        if (sigma == 0) return;
        var lower = (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1));
        if (lower % 2 == 0) lower--;
        var lowerCount = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4d * lower - 4));
        var temporary = new byte[pixels.Length];
        // Three box passes approximate a Gaussian; clamped edges avoid transparent seams.
        for (var pass = 0; pass < 3; pass++)
        {
            var radius = ((pass < lowerCount ? lower : lower + 2) - 1) / 2;
            if (radius == 0) continue;
            BoxPass(pixels, temporary, width, height, radius, true, token);
            BoxPass(temporary, pixels, width, height, radius, false, token);
        }
    }

    private static void BoxPass(byte[] input, byte[] output, int width, int height, int radius, bool horizontal, CancellationToken token)
    {
        var lines = horizontal ? height : width; var length = horizontal ? width : height;
        var stride = horizontal ? 4 : width * 4; var window = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            token.ThrowIfCancellationRequested();
            var start = horizontal ? line * width * 4 : line * 4;
            int blue = 0, green = 0, red = 0, alpha = 0;
            for (var sample = -radius; sample <= radius; sample++)
            {
                var index = start + Math.Clamp(sample, 0, length - 1) * stride;
                blue += input[index]; green += input[index + 1]; red += input[index + 2]; alpha += input[index + 3];
            }
            for (var position = 0; position < length; position++)
            {
                var index = start + position * stride;
                output[index] = (byte)((blue + window / 2) / window); output[index + 1] = (byte)((green + window / 2) / window);
                output[index + 2] = (byte)((red + window / 2) / window); output[index + 3] = (byte)((alpha + window / 2) / window);
                var leaving = start + Math.Max(0, position - radius) * stride;
                var entering = start + Math.Min(length - 1, position + radius + 1) * stride;
                blue += input[entering] - input[leaving]; green += input[entering + 1] - input[leaving + 1];
                red += input[entering + 2] - input[leaving + 2]; alpha += input[entering + 3] - input[leaving + 3];
            }
        }
    }
}
