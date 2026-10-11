using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace JavaSelect.App.UI;

public sealed class AmbientGlow : Grid
{
    private ContainerVisual? glow;
    public AmbientGlow()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => RenderGlow();
        ActualThemeChanged += (_, _) => { if (IsLoaded) RenderGlow(); };
        Unloaded += (_, _) => { ElementCompositionPreview.SetElementChildVisual(this, null); glow?.Dispose(); glow = null; };
    }
    private void RenderGlow()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null); glow?.Dispose();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        glow = compositor.CreateContainerVisual(); glow.RelativeSizeAdjustment = Vector2.One;
        var dark = ActualTheme == ElementTheme.Dark;
        Add(new Vector2(.12f, .05f), new Vector2(.7f, .66f), dark ? Color.FromArgb(34, 78, 139, 206) : Color.FromArgb(54, 178, 212, 248));
        Add(new Vector2(.91f, .21f), new Vector2(.57f, .64f), dark ? Color.FromArgb(24, 171, 105, 182) : Color.FromArgb(42, 216, 189, 239));
        ElementCompositionPreview.SetElementChildVisual(this, glow);
        void Add(Vector2 center, Vector2 radius, Color tint)
        {
            var brush = compositor.CreateRadialGradientBrush(); brush.EllipseCenter = center; brush.EllipseRadius = radius;
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0, tint));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(.5f, Color.FromArgb((byte)(tint.A * .35), tint.R, tint.G, tint.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, tint.R, tint.G, tint.B)));
            var light = compositor.CreateSpriteVisual(); light.RelativeSizeAdjustment = Vector2.One; light.Brush = brush;
            glow.Children.InsertAtTop(light);
        }
    }
}

public sealed class AvatarPortrait : Grid
{
    private const int Resolution = 224;
    private static readonly ConcurrentDictionary<string, Task<byte[]>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Image portrait;
    private readonly Border frame;
    private readonly bool fadeRight;
    private byte[]? sourcePixels;
    private int renderedWidth, renderedHeight;

    public AvatarPortrait(string path, double size) : this(path, size, size) { }

    public AvatarPortrait(string path, double width, double height, bool fadeRight = false)
    {
        Width = width; Height = height; this.fadeRight = fadeRight;
        portrait = new Image
        {
            Width = double.IsNaN(width) ? 136 : width, Height = double.IsNaN(height) ? 270 : height, Stretch = Stretch.UniformToFill,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/avatar.png"))
        };
        frame = new Border { Width = portrait.Width, Height = portrait.Height, CornerRadius = fadeRight ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(12), Child = portrait };
        var surface = new Canvas(); surface.Children.Add(frame); Children.Add(surface);
        SizeChanged += (_, _) => RenderPortrait();
        var loading = false;
        Loaded += async (_, _) =>
        {
            if (sourcePixels is not null || loading) return;
            loading = true;
            try
            {
                sourcePixels = await Cache.GetOrAdd(Path.GetFullPath(path), LoadAsync);
                RenderPortrait();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            finally { loading = false; }
        };
    }
    private void RenderPortrait()
    {
        var width = ActualWidth > 0 ? ActualWidth : portrait.Width;
        var height = ActualHeight > 0 ? ActualHeight : portrait.Height;
        portrait.Width = frame.Width = width; portrait.Height = frame.Height = height;
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
        if (sourcePixels is null || width <= 0 || height <= 0) return;
        var aspect = width / height;
        var columns = Math.Clamp((int)Math.Round(Resolution * Math.Min(1, aspect)), 1, Resolution);
        var rows = Math.Clamp((int)Math.Round(Resolution / Math.Max(1, aspect)), 1, Resolution);
        if (renderedWidth == columns && renderedHeight == rows) return;
        var pixels = new byte[columns * rows * 4];
        var left = (Resolution - columns) / 2; var top = (Resolution - rows) / 2;
        for (var y = 0; y < rows; y++) Buffer.BlockCopy(sourcePixels, ((top + y) * Resolution + left) * 4, pixels, y * columns * 4, columns * 4);
        if (fadeRight)
        {
            for (var x = 0; x < columns; x++)
            {
                var blend = Math.Clamp((x / (double)Math.Max(1, columns - 1) - .6) / .4, 0, 1);
                var opacity = 1 - blend * blend * (3 - 2 * blend);
                for (var y = 0; y < rows; y++) for (var channel = 0; channel < 4; channel++)
                {
                    var index = (y * columns + x) * 4 + channel;
                    pixels[index] = (byte)Math.Round(pixels[index] * opacity);
                }
            }
        }
        var bitmap = new WriteableBitmap(columns, rows);
        using (var buffer = bitmap.PixelBuffer.AsStream())
        {
            buffer.Position = 0; buffer.Write(pixels, 0, pixels.Length); buffer.Flush();
        }
        bitmap.Invalidate(); portrait.Source = bitmap;
        renderedWidth = columns; renderedHeight = rows;
    }
    private static async Task<byte[]> LoadAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = Resolution, ScaledHeight = Resolution }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        return await Task.Run(() => BlurLowerHalf(pixels));
    }
    private static byte[] BlurLowerHalf(byte[] source)
    {
        const int radius = 14;
        var kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(x => Math.Exp(-x * x / 60.0)).ToArray();
        var sum = kernel.Sum(); for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var horizontal = new byte[source.Length]; var blurred = new byte[source.Length];
        Pass(source, horizontal, true); Pass(horizontal, blurred, false);
        for (var y = 0; y < Resolution; y++)
        {
            var blend = Math.Clamp((y / (double)Resolution - .4) / .58, 0, 1);
            blend = blend * blend * (3 - 2 * blend);
            for (var x = 0; x < Resolution * 4; x++)
            {
                var index = y * Resolution * 4 + x;
                blurred[index] = (byte)Math.Round(source[index] * (1 - blend) + blurred[index] * blend);
            }
        }
        return blurred;
        void Pass(byte[] input, byte[] output, bool across)
        {
            for (var y = 0; y < Resolution; y++) for (var x = 0; x < Resolution; x++) for (var channel = 0; channel < 4; channel++)
            {
                double value = 0;
                for (var step = -radius; step <= radius; step++)
                {
                    var px = across ? Math.Clamp(x + step, 0, Resolution - 1) : x;
                    var py = across ? y : Math.Clamp(y + step, 0, Resolution - 1);
                    value += input[(py * Resolution + px) * 4 + channel] * kernel[step + radius];
                }
                output[(y * Resolution + x) * 4 + channel] = (byte)Math.Round(value);
            }
        }
    }
}
