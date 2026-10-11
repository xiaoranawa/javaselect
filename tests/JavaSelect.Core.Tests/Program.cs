using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JavaSelect.Core;
using JavaSelect.App.Services;

if (args.Contains("-version"))
{
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "timeout"))) await Task.Delay(5000);
    var counterPath = Path.Combine(AppContext.BaseDirectory, "concurrency.txt");
    if (File.Exists(counterPath))
    {
        using var mutex = new Mutex(false, "JavaSelectTest-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..24]);
        mutex.WaitOne();
        var counts = File.ReadAllText(counterPath).Split(',').Select(int.Parse).ToArray();
        File.WriteAllText(counterPath, $"{counts[0] + 1},{Math.Max(counts[1], counts[0] + 1)}");
        mutex.ReleaseMutex();
        await Task.Delay(250);
        mutex.WaitOne(); counts = File.ReadAllText(counterPath).Split(',').Select(int.Parse).ToArray();
        File.WriteAllText(counterPath, $"{counts[0] - 1},{counts[1]}"); mutex.ReleaseMutex();
    }
    Console.Error.WriteLine("java.version = 1.8.0_202\njava.vendor = Oracle Corporation\nos.arch = amd64\njava version \"1.8.0_202\"");
    return;
}
if (args.Length == 2 && args[0] == "--probe")
{
    var item = await new JavaDiscoveryService().ProbeAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = item?.IsValid == true ? 0 : 1; return;
}

var temporaryRoot = Path.Combine(AppContext.BaseDirectory, "JavaSelect-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
int passed = 0, failed = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
async Task Throws<T>(Func<Task> body) where T : Exception
{
    try { await body(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
EnvironmentSnapshot Snapshot() => new(new(@"C:\OldJava"), new(@"C:\Tools;%USERPROFILE%\bin;;", true, true), new(@"C:\MachineJava"), new(@"C:\Windows\System32"), new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\test" });
JavaInstallation Target() => new() { Id = "new", Path = @"C:\NewJava", Version = "21.0.11", MajorVersion = 21, Vendor = "Oracle", Kind = "JDK", IsValid = true, Fingerprint = "stable" };
ConfigStore Store(string name) => new(Path.Combine(temporaryRoot, name));
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jQz8AAAAASUVORK5CYII=");
Task AcceptImage(string _, CancellationToken __) => Task.CompletedTask;

try
{
    await UpdateServiceTests.RunAsync(temporaryRoot, Check, CreateJunctionAsync);
    await Check("Java versions and architecture", () =>
    {
        Equal(8, JavaDiscoveryService.ParseMajorVersion("1.8.0_202")); Equal(21, JavaDiscoveryService.ParseMajorVersion("21.0.11+9-LTS"));
        Equal(26, JavaDiscoveryService.ParseMajorVersion("26-ea")); Equal(0, JavaDiscoveryService.ParseMajorVersion("invalid"));
        Equal("17.0.12", JavaDiscoveryService.ParseVersion("openjdk version \"17.0.12\""));
        Equal("x64", JavaDiscoveryService.NormalizeArchitecture("amd64")); Equal("ARM64", JavaDiscoveryService.NormalizeArchitecture("aarch64"));
        return Task.CompletedTask;
    });
    await Check("Locale mapping distinguishes scripts and regions", () =>
    {
        foreach (var (input, expected) in new[] { ("zh-CN", "zh-CN"), ("zh-HK", "zh-TW"), ("zh-Hans-HK", "zh-CN"), ("zh-Hant-CN", "zh-TW"), ("ja-JP", "ja-JP"), ("fr-FR", "en-US") }) Equal(expected, LocaleResolver.Resolve(input));
        return Task.CompletedTask;
    });
    await Check("Search combines normalized terms and manufacturer", () =>
    {
        JavaInstallation[] items = [new() { Version = "21.0.11", MajorVersion = 21, Kind = "JDK", Vendor = "Oracle", Path = @"E:\JDK\jdk-21" }, new() { MajorVersion = 17, Kind = "JRE", Vendor = "Eclipse Adoptium" }];
        Equal(1, InstallationFilter.Filter(items, "ＪＤＫ２１ E:/JDK", "Oracle").Count()); Equal(0, InstallationFilter.Filter(items, "21", "Eclipse Adoptium").Count());
        Equal(2, InstallationFilter.GetVendors(items).Count); Equal(1, InstallationFilter.GetVendors(items.Take(1)).Count); Equal(0, InstallationFilter.Filter(items, "*").Count());
        return Task.CompletedTask;
    });
    await Check("Shortcut parser rejects duplicate and missing modifiers", () =>
    {
        True(HotkeyGesture.TryParse("Alt+J", out var key)); Equal(1u, key!.Modifiers); Equal((uint)'J', key.VirtualKey);
        True(HotkeyGesture.TryParse("Ctrl+F24", out key)); Equal(0x87u, key!.VirtualKey);
        True(!HotkeyGesture.TryParse("Alt++J", out _)); True(!HotkeyGesture.TryParse("Alt+Alt+J", out _)); True(!HotkeyGesture.TryParse("J", out _));
        return Task.CompletedTask;
    });
    await Check("Native launcher radius defaults to ten and persists independently", async () =>
    {
        Equal(10d, new AppSettings().OverlayCornerRadius);
        var store = Store("radius-native"); var settings = await store.LoadAsync();
        Equal(10d, settings.OverlayCornerRadius);
        settings.OverlayCornerRadius = 14.5; settings.OverlayOpacity = 56;
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Equal(14.5, loaded.OverlayCornerRadius); Equal(56d, loaded.OverlayOpacity);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
        Equal(14.5, document.RootElement.GetProperty("OverlayCornerRadius").GetDouble());
        True(!document.RootElement.TryGetProperty("OverlayCss", out _));
    });
    await Check("Legacy launcher radius migrates and removes retired CSS on save", async () =>
    {
        var cases = new[]
        {
            (".switcher { --radius: 0px; }", 0d),
            (".switcher { --radius: 40px; }", 40d),
            (".switcher { --accent: #ff9e32; --radius: 18px; --gap: 8px; --padding: 18px; }", 18d),
            (".switcher { --radius: 8px; --radius: 20px; }", 20d)
        };
        var index = 0;
        foreach (var (css, expected) in cases)
        {
            var store = Store("radius-legacy-" + index++); await store.LoadAsync();
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { overlaycss = css, Language = "ja-JP", OverlayOpacity = 56 }));
            var loaded = await store.LoadAsync();
            Equal(expected, loaded.OverlayCornerRadius); Equal("ja-JP", loaded.Language); Equal(56d, loaded.OverlayOpacity);
            await store.SaveAsync(loaded);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
            True(!document.RootElement.EnumerateObject().Any(x => x.Name.Equals("OverlayCss", StringComparison.OrdinalIgnoreCase)));
            Equal(expected, (await store.LoadAsync()).OverlayCornerRadius);
        }
    });
    await Check("Explicit native launcher radius takes precedence over legacy CSS", async () =>
    {
        var store = Store("radius-precedence"); await store.LoadAsync();
        foreach (var (native, expected) in new[] { (0d, 0d), (10d, 10d), (-5d, 0d), (99d, 40d) })
        {
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { OverlayCss = ".switcher { --radius: 30px; }", overlaycornerradius = native }));
            Equal(expected, (await store.LoadAsync()).OverlayCornerRadius);
        }
    });
    await Check("Damaged legacy styles safely fall back to the default radius", async () =>
    {
        object?[] cases =
        [
            null, 123, "", "broken", ".switcher { --gap: 8px; }",
            ".switcher { --radius: -1px; }", ".switcher { --radius: 1.5px; }",
            ".switcher { --radius: 41px; }", ".switcher { --radius: 10em; }",
            ".switcher { --radius: 20px; --radius: broken; }",
            ".switcher { --radius: broken; --radius: 20px; }",
            ".switcher { --radius: 20px; broken }", ".other { --radius: 20px; }",
            ".switcher { --radius: 20px; } .switcher { --radius: 30px; }",
            ".switcher { --radius: 20px; }" + new string(' ', 4096)
        ];
        var store = Store("radius-damaged"); await store.LoadAsync();
        foreach (var css in cases)
        {
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { OverlayCss = css }));
            Equal(10d, (await store.LoadAsync()).OverlayCornerRadius);
        }
    });
    await Check("Native launcher radius normalization bounds finite values", async () =>
    {
        var store = Store("radius-bounds");
        foreach (var (value, expected) in new[] { (-1d, 0d), (0d, 0d), (40d, 40d), (41d, 40d), (double.NaN, 10d), (double.PositiveInfinity, 10d), (double.NegativeInfinity, 10d) })
        {
            var settings = new AppSettings { OverlayCornerRadius = value };
            await store.SaveAsync(settings);
            Equal(expected, settings.OverlayCornerRadius);
            Equal(expected, (await store.LoadAsync()).OverlayCornerRadius);
        }
    });
    await Check("Optional launcher appearance starts disabled in new and existing configurations", async () =>
    {
        void CheckDefaults(AppSettings settings)
        {
            True(!settings.OverlayImageBlurEnabled); Equal(5d, settings.OverlayImageBlur);
            True(!settings.OverlayTextBackground); Equal("frosted", settings.OverlayBackgroundStyle);
            True(settings.OverlayImagePath is null);
        }
        CheckDefaults(new AppSettings());
        var store = Store("image-blur-default"); CheckDefaults(await store.LoadAsync());
        await File.WriteAllTextAsync(store.FilePath, """{"OverlayOpacity":72,"OverlayCornerRadius":18}""");
        var loaded = await store.LoadAsync();
        CheckDefaults(loaded); Equal(72d, loaded.OverlayOpacity); Equal(18d, loaded.OverlayCornerRadius);
        loaded.OverlayImageBlur = 0; await store.SaveAsync(loaded);
        Equal(0d, (await store.LoadAsync()).OverlayImageBlur);
    });
    await Check("Image blur enablement persists without resetting its configured strength", async () =>
    {
        var store = Store("image-blur-toggle"); var settings = await store.LoadAsync();
        settings.OverlayImageBlurEnabled = true; await store.SaveAsync(settings);
        var loaded = await store.LoadAsync(); True(loaded.OverlayImageBlurEnabled); Equal(5d, loaded.OverlayImageBlur);
        loaded.OverlayImageBlur = 12.5; loaded.OverlayImageBlurEnabled = false; await store.SaveAsync(loaded);
        loaded = await store.LoadAsync(); True(!loaded.OverlayImageBlurEnabled); Equal(12.5, loaded.OverlayImageBlur);
        loaded.OverlayImageBlurEnabled = true; await store.SaveAsync(loaded);
        loaded = await store.LoadAsync(); True(loaded.OverlayImageBlurEnabled); Equal(12.5, loaded.OverlayImageBlur);
    });
    await Check("Legacy image blur preserves only explicitly configured image effects", async () =>
    {
        var store = Store("image-blur-legacy"); await store.LoadAsync();
        var directory = Path.Combine(store.DirectoryPath, "backgrounds"); Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "background-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(imagePath, png);
        foreach (var (style, strength, enabled) in new (string, double?, bool)[]
        {
            ("image", 5, true), ("image", 12.5, true), ("image", 99, true),
            ("image", 0, false), ("image", -1, false), ("image", null, false),
            ("frosted", 5, false), ("solid", 5, false)
        })
        {
            var document = new Dictionary<string, object?> { ["OverlayBackgroundStyle"] = style, ["OverlayImagePath"] = imagePath };
            if (strength is { } value) document["overlayimageblur"] = value;
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(document));
            var loaded = await store.LoadAsync(); Equal(enabled, loaded.OverlayImageBlurEnabled);
            Equal(strength is { } radius ? Math.Clamp(radius, 0, 30) : 5, loaded.OverlayImageBlur);
            await store.SaveAsync(loaded); Equal(enabled, (await store.LoadAsync()).OverlayImageBlurEnabled);
        }
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new
        {
            OverlayBackgroundStyle = "image", OverlayImagePath = imagePath, OverlayImageBlur = 5, overlayimageblurenabled = false
        }));
        var explicitChoice = await store.LoadAsync(); True(!explicitChoice.OverlayImageBlurEnabled);
        await store.SaveAsync(explicitChoice); True(!(await store.LoadAsync()).OverlayImageBlurEnabled);
    });
    await Check("Image blur persists and normalizes bounds independently of background mode", async () =>
    {
        var store = Store("image-blur-bounds");
        foreach (var (value, expected) in new[] { (-1d, 0d), (0d, 0d), (12.5, 12.5), (30d, 30d), (31d, 30d), (double.NaN, 5d), (double.PositiveInfinity, 5d), (double.NegativeInfinity, 5d) })
        {
            var settings = new AppSettings { OverlayImageBlur = value, OverlayBackgroundStyle = "solid" };
            await store.SaveAsync(settings);
            Equal(expected, settings.OverlayImageBlur); Equal(expected, (await store.LoadAsync()).OverlayImageBlur);
        }
    });
    await Check("Image blur zero preserves pixels and clamped edges preserve uniform color", () =>
    {
        var original = Enumerable.Range(0, 12).SelectMany(_ => new byte[] { 12, 38, 70, 125 }).ToArray();
        var pixels = original.ToArray();
        LauncherBackgroundPixels.Blur(pixels, 4, 3, 0, default); True(original.SequenceEqual(pixels));
        LauncherBackgroundPixels.Blur(pixels, 4, 3, 30, default); True(original.SequenceEqual(pixels));
        byte[] single = [9, 22, 48, 72]; LauncherBackgroundPixels.Blur(single, 1, 1, 120, default);
        True(single.SequenceEqual(new byte[] { 9, 22, 48, 72 }));
        return Task.CompletedTask;
    });
    await Check("Image blur softens edges symmetrically and preserves premultiplied alpha", () =>
    {
        const int width = 101;
        var pixels = new byte[width * 4];
        for (var x = 30; x <= 70; x++) { pixels[x * 4] = 64; pixels[x * 4 + 1] = 100; pixels[x * 4 + 2] = 128; pixels[x * 4 + 3] = 128; }
        var moreBlurred = pixels.ToArray();
        LauncherBackgroundPixels.Blur(pixels, width, 1, 3, default);
        LauncherBackgroundPixels.Blur(moreBlurred, width, 1, 15, default);
        True(pixels[29 * 4 + 3] > 0 && pixels[30 * 4 + 3] < 128);
        True(moreBlurred[20 * 4 + 3] > pixels[20 * 4 + 3]);
        for (var x = 0; x < width; x++) for (var c = 0; c < 4; c++)
        {
            Equal(pixels[x * 4 + c], pixels[(width - 1 - x) * 4 + c]);
            True(pixels[x * 4 + c] <= pixels[x * 4 + 3]);
        }
        return Task.CompletedTask;
    });
    await Check("Background resize interpolates colors without stretching edge transparency", () =>
    {
        byte[] source = [0, 0, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255];
        var resized = LauncherBackgroundPixels.Resize(source, 2, 2, 3, 3, default);
        Equal(36, resized.Length); Equal((byte)64, resized[16]); Equal((byte)64, resized[17]); Equal((byte)64, resized[18]);
        for (var x = 3; x < resized.Length; x += 4) Equal((byte)255, resized[x]);
        True(resized.AsSpan(0, 4).SequenceEqual(source.AsSpan(0, 4)));
        True(resized.AsSpan(32, 4).SequenceEqual(source.AsSpan(12, 4)));
        return Task.CompletedTask;
    });
    await Check("Canceled background rendering stops before processing pixels", async () =>
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => { LauncherBackgroundPixels.Blur(new byte[16], 2, 2, 5, cancellation.Token); return Task.CompletedTask; });
        await Throws<OperationCanceledException>(() => { LauncherBackgroundPixels.Resize(new byte[16], 2, 2, 3, 3, cancellation.Token); return Task.CompletedTask; });
    });
    await Check("Text background defaults also apply to existing configurations", async () =>
    {
        var defaults = new AppSettings();
        True(!defaults.OverlayTextBackground);
        Equal("#FFFFFF", defaults.OverlayTextBackgroundColor); Equal(100d, defaults.OverlayTextBackgroundOpacity); Equal(10d, defaults.OverlayTextBackgroundCornerRadius);
        var store = Store("text-background-legacy"); await store.LoadAsync();
        await File.WriteAllTextAsync(store.FilePath, """{"SchemaVersion":1,"OverlayTextBackground":true,"OverlayOpacity":56,"OverlayCornerRadius":24}""");
        var loaded = await store.LoadAsync();
        True(loaded.OverlayTextBackground); Equal("#FFFFFF", loaded.OverlayTextBackgroundColor); Equal(100d, loaded.OverlayTextBackgroundOpacity); Equal(10d, loaded.OverlayTextBackgroundCornerRadius);
        Equal(56d, loaded.OverlayOpacity); Equal(24d, loaded.OverlayCornerRadius);
    });
    await Check("Text background appearance persists independently while disabled", async () =>
    {
        var store = Store("text-background-persistence");
        var settings = new AppSettings
        {
            OverlayTextBackground = true, OverlayTextBackgroundColor = "#123aBC", OverlayTextBackgroundOpacity = 37.5, OverlayTextBackgroundCornerRadius = 6.5,
            OverlaySolidColor = "#FEDCBA", OverlayOpacity = 81, OverlayCornerRadius = 28, OverlayTextColor = "black"
        };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        True(loaded.OverlayTextBackground);
        loaded.OverlayTextBackground = false; await store.SaveAsync(loaded); loaded = await store.LoadAsync();
        True(!loaded.OverlayTextBackground); Equal("#123aBC", loaded.OverlayTextBackgroundColor); Equal(37.5, loaded.OverlayTextBackgroundOpacity); Equal(6.5, loaded.OverlayTextBackgroundCornerRadius);
        Equal("#FEDCBA", loaded.OverlaySolidColor); Equal(81d, loaded.OverlayOpacity); Equal(28d, loaded.OverlayCornerRadius); Equal("black", loaded.OverlayTextColor);
    });
    await Check("Text background opacity and radius normalize invalid values and preserve endpoints", async () =>
    {
        var store = Store("text-background-bounds");
        foreach (var (opacity, radius, expectedOpacity, expectedRadius) in new[]
        {
            (-1d, -1d, 0d, 0d), (0d, 0d, 0d, 0d), (100d, 40d, 100d, 40d), (101d, 41d, 100d, 40d),
            (double.NaN, double.NaN, 100d, 10d), (double.PositiveInfinity, double.PositiveInfinity, 100d, 10d), (double.NegativeInfinity, double.NegativeInfinity, 100d, 10d)
        })
        {
            var settings = new AppSettings { OverlayTextBackgroundOpacity = opacity, OverlayTextBackgroundCornerRadius = radius };
            await store.SaveAsync(settings);
            Equal(expectedOpacity, settings.OverlayTextBackgroundOpacity); Equal(expectedRadius, settings.OverlayTextBackgroundCornerRadius);
            var loaded = await store.LoadAsync();
            Equal(expectedOpacity, loaded.OverlayTextBackgroundOpacity); Equal(expectedRadius, loaded.OverlayTextBackgroundCornerRadius);
        }
        await File.WriteAllTextAsync(store.FilePath, """{"OverlayTextBackgroundOpacity":-10,"OverlayTextBackgroundCornerRadius":90}""");
        var bounded = await store.LoadAsync();
        Equal(0d, bounded.OverlayTextBackgroundOpacity); Equal(40d, bounded.OverlayTextBackgroundCornerRadius);
    });
    await Check("Text background color accepts only complete RGB hex colors", async () =>
    {
        var store = Store("text-background-color"); await store.LoadAsync();
        foreach (var color in new string?[] { null, "", "white", "#fff", "#FFFFFFFF", "#12345g", "123456", "#FFFFFF\n", " #FFFFFF" })
        {
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { OverlayTextBackgroundColor = color }));
            Equal("#FFFFFF", (await store.LoadAsync()).OverlayTextBackgroundColor);
        }
        foreach (var color in new[] { "#000000", "#FFFFFF", "#aBcDeF" })
        {
            await store.SaveAsync(new AppSettings { OverlayTextBackgroundColor = color });
            Equal(color, (await store.LoadAsync()).OverlayTextBackgroundColor);
        }
    });
    await Check("Launcher selection color follows the system by default", async () =>
    {
        Equal("auto", new AppSettings().OverlaySelectionColor);
        Equal("auto", new AppSettings().OverlaySelectionBackgroundColor);
        var store = Store("selection-color-default"); var initial = await store.LoadAsync();
        Equal("auto", initial.OverlaySelectionColor); Equal("auto", initial.OverlaySelectionBackgroundColor);
        await File.WriteAllTextAsync(store.FilePath, """{"OverlayTextColor":"white","OverlayOpacity":72}""");
        var loaded = await store.LoadAsync(); Equal("auto", loaded.OverlaySelectionColor);
        Equal("auto", loaded.OverlaySelectionBackgroundColor);
        Equal("white", loaded.OverlayTextColor); Equal(72d, loaded.OverlayOpacity);
    });
    await Check("Launcher selection color persists automatic and custom RGB choices", async () =>
    {
        var store = Store("selection-color-persistence");
        foreach (var color in new[] { "#000000", "#FFFFFF", "#aBcDeF", "auto" })
        {
            await store.SaveAsync(new AppSettings { OverlaySelectionColor = color });
            Equal(color, (await store.LoadAsync()).OverlaySelectionColor);
        }
    });
    await Check("Invalid launcher selection colors fall back to the system accent", async () =>
    {
        var store = Store("selection-color-invalid"); await store.LoadAsync();
        foreach (var color in new string?[] { null, "", "AUTO", "white", "#fff", "#FFFFFFFF", "#12345g", "123456", "#FFFFFF\n", " #FFFFFF", "auto " })
        {
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { OverlaySelectionColor = color }));
            Equal("auto", (await store.LoadAsync()).OverlaySelectionColor);
            var settings = new AppSettings { OverlaySelectionColor = color! }; await store.SaveAsync(settings);
            Equal("auto", settings.OverlaySelectionColor); Equal("auto", (await store.LoadAsync()).OverlaySelectionColor);
        }
    });
    await Check("Launcher selection fill persists independently of its border color", async () =>
    {
        var store = Store("selection-fill-persistence");
        foreach (var color in new[] { "#000000", "#FFFFFF", "#aBcDeF", "auto" })
        {
            await store.SaveAsync(new AppSettings { OverlaySelectionBackgroundColor = color, OverlaySelectionColor = "#123456" });
            var loaded = await store.LoadAsync(); Equal(color, loaded.OverlaySelectionBackgroundColor);
            Equal("#123456", loaded.OverlaySelectionColor);
        }
    });
    await Check("Invalid launcher selection fill colors fall back to automatic", async () =>
    {
        var store = Store("selection-fill-invalid"); await store.LoadAsync();
        foreach (var color in new string?[] { null, "", "AUTO", "white", "#fff", "#FFFFFFFF", "#12345g", "123456", "#FFFFFF\n", " #FFFFFF", "auto " })
        {
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { OverlaySelectionBackgroundColor = color }));
            Equal("auto", (await store.LoadAsync()).OverlaySelectionBackgroundColor);
            var settings = new AppSettings { OverlaySelectionBackgroundColor = color! }; await store.SaveAsync(settings);
            Equal("auto", settings.OverlaySelectionBackgroundColor); Equal("auto", (await store.LoadAsync()).OverlaySelectionBackgroundColor);
        }
    });
    await Check("Appearance reset restores defaults while preserving cached images and all unrelated settings", async () =>
    {
        var store = Store("appearance-reset");
        var directory = Path.Combine(store.DirectoryPath, "backgrounds"); Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "background-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(imagePath, png);
        var installation = Target(); installation.Hotkey = "Ctrl+F2"; installation.ShowInLauncher = false;
        var settings = new AppSettings
        {
            Theme = "dark", OverlayBackgroundStyle = "image", OverlayOpacity = 55, OverlaySolidColor = "#A1B2C3", OverlayTextColor = "white",
            OverlaySelectionColor = "#123456", OverlaySelectionBackgroundColor = "#654321", OverlayTextBackground = true,
            OverlayTextBackgroundColor = "#FEDCBA", OverlayTextBackgroundOpacity = 35, OverlayTextBackgroundCornerRadius = 25,
            OverlayImageBlurEnabled = true, OverlayImageBlur = 17, OverlayCornerRadius = 28, OverlayImagePath = imagePath, OverlayImageName = "custom.png",
            Language = "ja-JP", OverlayHotkey = "Ctrl+F3", SwitchMode = "instant", WrapSelection = false,
            StartAtLogin = true, StartInTray = true, CloseToTray = false, AutomaticTerminalSync = true, KeepCurrentJavaSelection = false,
            SwitchNotifications = false, DiscoveryNotifications = false, RestoreNotifications = false, AutoUpdate = false,
            LastSelectedId = installation.Id, LastSelectedFingerprint = installation.Fingerprint, ManagedPathEntry = @"C:\ManagedJava\bin", Installations = [installation]
        };
        var before = JsonSerializer.SerializeToElement(settings);
        var defaults = JsonSerializer.SerializeToElement(new AppSettings());
        void CheckReset(AppSettings actual)
        {
            var current = JsonSerializer.SerializeToElement(actual);
            foreach (var property in before.EnumerateObject())
            {
                var appearance = property.Name == nameof(AppSettings.Theme) || property.Name.StartsWith("Overlay", StringComparison.Ordinal)
                    && property.Name is not (nameof(AppSettings.OverlayHotkey) or nameof(AppSettings.OverlayImagePath) or nameof(AppSettings.OverlayImageName));
                var expected = appearance ? defaults.GetProperty(property.Name) : property.Value;
                Equal(expected.GetRawText(), current.GetProperty(property.Name).GetRawText());
            }
        }
        settings.ResetAppearance(); CheckReset(settings);
        True(ReferenceEquals(installation, settings.Installations[0]));
        settings.ResetAppearance(); CheckReset(settings);
        await store.SaveAsync(settings); CheckReset(await store.LoadAsync());
        True((await File.ReadAllBytesAsync(imagePath)).SequenceEqual(png));
    });
    await Check("Initial locale and atomic manual preference persistence", async () =>
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-HK"); var store = Store("language"); var settings = await store.LoadAsync(); Equal("zh-TW", settings.Language);
            settings.Language = "ja-JP"; settings.OverlayOpacity = 70; await store.SaveAsync(settings);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US"); Equal("ja-JP", (await store.LoadAsync()).Language); Equal(70d, (await store.LoadAsync()).OverlayOpacity);
            Equal(1, Directory.GetFiles(store.DirectoryPath).Length);
        }
        finally { CultureInfo.CurrentUICulture = before; }
    });
    await Check("Launcher visibility persists independently for each Java installation", async () =>
    {
        var store = Store("launcher-visibility");
        var hidden = Target(); hidden.ShowInLauncher = false;
        var visible = Target(); visible.Id = "visible";
        await store.SaveAsync(new AppSettings { Installations = [hidden, visible] });
        var loaded = await store.LoadAsync();
        True(!loaded.Installations.Single(x => x.Id == hidden.Id).ShowInLauncher);
        True(loaded.Installations.Single(x => x.Id == visible.Id).ShowInLauncher);
    });
    await Check("Existing configurations show Java installations in the launcher by default", async () =>
    {
        var store = Store("launcher-legacy"); await store.LoadAsync();
        await File.WriteAllTextAsync(store.FilePath, """{"SchemaVersion":1,"Installations":[{"Id":"legacy","Path":"C:\\LegacyJava","IsValid":true}]}""");
        var loaded = await store.LoadAsync();
        True(loaded.Installations.Single().ShowInLauncher);
        await store.SaveAsync(loaded);
        True((await store.LoadAsync()).Installations.Single().ShowInLauncher);
    });
    await Check("Invalid configuration is preserved", async () =>
    {
        var store = Store("invalid"); await store.LoadAsync(); await File.WriteAllTextAsync(store.FilePath, "broken");
        await Throws<JsonException>(() => store.LoadAsync()); Equal("broken", await File.ReadAllTextAsync(store.FilePath));
    });
    await Check("Background copy uses relative persistence and removes only previous owned file", async () =>
    {
        var store = Store("image"); var settings = await store.LoadAsync(); var source = Path.Combine(temporaryRoot, "input.png"); await File.WriteAllBytesAsync(source, png);
        var first = await store.ImportBackgroundAsync(source, settings, AcceptImage); File.Delete(source); True(File.Exists(first));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath))) True(!Path.IsPathRooted(document.RootElement.GetProperty("OverlayImagePath").GetString()!));
        Equal(first, (await store.LoadAsync()).OverlayImagePath);
        await File.WriteAllBytesAsync(source, png); var second = await store.ImportBackgroundAsync(source, settings, AcceptImage); True(!File.Exists(first)); True(File.Exists(second));
        await store.RemoveBackgroundAsync(settings); True(!File.Exists(second)); True(File.Exists(source));
    });
    await Check("Copied-image validation failure preserves old image and configuration", async () =>
    {
        var store = Store("decode"); var settings = await store.LoadAsync(); var source = Path.Combine(temporaryRoot, "decode.png"); await File.WriteAllBytesAsync(source, png);
        var previous = await store.ImportBackgroundAsync(source, settings, AcceptImage); var before = await File.ReadAllTextAsync(store.FilePath);
        await Throws<InvalidDataException>(() => store.ImportBackgroundAsync(source, settings, (copied, _) => { True(copied != source); True(File.Exists(copied)); throw new InvalidDataException("Decoder failed."); }));
        Equal(previous, settings.OverlayImagePath); True(File.Exists(previous)); Equal(before, await File.ReadAllTextAsync(store.FilePath)); Equal(1, Directory.GetFiles(Path.Combine(store.DirectoryPath, "backgrounds")).Length);
    });
    await Check("External and traversal image paths are not read or deleted", async () =>
    {
        var store = Store("external"); var settings = await store.LoadAsync(); var external = Path.Combine(temporaryRoot, "background-" + Guid.NewGuid().ToString("N") + ".png"); await File.WriteAllBytesAsync(external, png);
        Equal<string?>(null, store.ResolveBackgroundPath(external)); Equal<string?>(null, store.ResolveBackgroundPath(Path.GetRelativePath(store.DirectoryPath, external)));
        settings.OverlayImagePath = external; await store.RemoveBackgroundAsync(settings); True(File.Exists(external));
    });
    if (OperatingSystem.IsWindows()) await Check("Background junction blocks read copy and delete redirection", async () =>
    {
        var store = Store("junction"); var settings = await store.LoadAsync(); var outside = Path.Combine(temporaryRoot, "junction-target"); Directory.CreateDirectory(outside);
        var external = Path.Combine(outside, "background-" + Guid.NewGuid().ToString("N") + ".png"); await File.WriteAllBytesAsync(external, png);
        var junction = Path.Combine(store.DirectoryPath, "backgrounds"); await CreateJunctionAsync(junction, outside);
        try
        {
            settings.OverlayImagePath = Path.Combine(junction, Path.GetFileName(external)); Equal<string?>(null, store.ResolveBackgroundPath(settings.OverlayImagePath));
            await store.RemoveBackgroundAsync(settings); True(File.Exists(external)); await Throws<IOException>(() => store.ImportBackgroundAsync(external, settings, AcceptImage)); Equal(1, Directory.GetFiles(outside).Length);
        }
        finally { Directory.Delete(junction); }
    });
    if (OperatingSystem.IsWindows()) await Check("Configuration root and ancestor junctions are rejected", async () =>
    {
        var target = Path.Combine(temporaryRoot, "root-target"); Directory.CreateDirectory(target); var link = Path.Combine(temporaryRoot, "root-link"); await CreateJunctionAsync(link, target);
        try { await Throws<IOException>(() => new ConfigStore(link).LoadAsync()); await Throws<IOException>(() => new ConfigStore(Path.Combine(link, "child")).LoadAsync()); Equal(0, Directory.GetFileSystemEntries(target).Length); }
        finally { Directory.Delete(link); }
    });
    await Check("PATH planning preserves unrelated entries and duplicates", () =>
    {
        Equal(@"C:\NewJava\bin;C:\Tools;%USERPROFILE%\bin;;", EnvironmentSwitchService.BuildUserPath(@"C:\OldJava\bin;C:\Tools;%USERPROFILE%\bin;;", @"C:\OldJava\bin", @"C:\NewJava\bin"));
        Equal(@"C:\NewJava\bin;C:\OldJava\bin", EnvironmentSwitchService.BuildUserPath(@"C:\OldJava\bin;C:\OldJava\bin", @"C:\OldJava\bin", @"C:\NewJava\bin")); return Task.CompletedTask;
    });
    await Check("Machine Oracle redirector rejects switch with zero writes", async () =>
    {
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(@"C:\Oracle\java8path;C:\Windows\System32") }); environment.Files.Add(@"C:\Oracle\java8path\java.exe"); var settings = new AppSettings { LastSelectedId = "old" };
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), settings);
        True(!result.Success); Equal(@"C:\Oracle\java8path\java.exe", result.ConflictPath); Equal(0, environment.Writes); Equal("old", settings.LastSelectedId);
    });
    await Check("Verified switch persists final selection fingerprint and refreshed metadata", async () =>
    {
        var environment = new FakeEnvironment(Snapshot()); var store = Store("switch"); var stale = Target(); stale.Version = "17.0.1"; stale.MajorVersion = 17; stale.Alias = "Work Java"; stale.Fingerprint = "old"; stale.ShowInLauncher = false;
        var settings = new AppSettings { Installations = [stale] }; var result = await new EnvironmentSwitchService(store, environment, new FakeProbe()).SwitchAsync(stale, settings);
        True(result.Success); Equal("new", settings.LastSelectedId); Equal("stable", settings.LastSelectedFingerprint); Equal(21, settings.Installations.Single().MajorVersion); Equal("Work Java", settings.Installations.Single().Alias);
        Equal("stable", (await store.LoadAsync()).Installations.Single().Fingerprint); Equal(@"C:\NewJava\bin", settings.ManagedPathEntry); Equal(1, environment.Broadcasts);
        True(!settings.Installations.Single().ShowInLauncher); True(!(await store.LoadAsync()).Installations.Single().ShowInLauncher);
    });
    await Check("Existing unowned first PATH entry is not claimed", async () =>
    {
        var environment = new FakeEnvironment(Snapshot() with { UserPath = new(@"C:\NewJava\bin;C:\Tools") }); var settings = new AppSettings();
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), settings);
        True(result.Success); Equal<string?>(null, settings.ManagedPathEntry);
    });
    await Check("Machine JAVA_HOME expansion cannot be overridden by the user scope", async () =>
    {
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(@"%JAVA_HOME%\bin;C:\Windows\System32", true, true) });
        environment.Files.Add(@"C:\MachineJava\bin\java.exe");
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), new AppSettings());
        True(!result.Success); Equal(@"C:\MachineJava\bin\java.exe", result.ConflictPath); Equal(0, environment.Writes);
    });
    await Check("User PATH expansion uses user JAVA_HOME and respects registry string type", () =>
    {
        var environment = new FakeEnvironment(Snapshot() with { UserJavaHome = new(@"C:\NewJava"), UserPath = new(@"%JAVA_HOME%\bin", true, true) });
        Equal(@"C:\NewJava\bin\java.exe", EnvironmentSwitchService.ResolveFirstJava(environment.State, environment.FileExists));
        Equal<string?>(null, EnvironmentSwitchService.ResolveFirstJava(environment.State with { UserPath = new(@"%JAVA_HOME%\bin", true, false) }, environment.FileExists));
        return Task.CompletedTask;
    });
    await Check("Machine expansion does not consume user-defined variables", () =>
    {
        var environment = new FakeEnvironment(Snapshot() with
        {
            MachinePath = new(@"%CUSTOM_JAVA%\bin", true, true), UserPath = new(@"C:\NewJava\bin"),
            UserVariables = new Dictionary<string, EnvironmentValue> { ["CUSTOM_JAVA"] = new(@"C:\OtherJava") }
        });
        environment.Files.Add(@"C:\OtherJava\bin\java.exe");
        Equal(@"C:\NewJava\bin\java.exe", EnvironmentSwitchService.ResolveFirstJava(environment.State, environment.FileExists));
        return Task.CompletedTask;
    });
    await Check("Partial failure restores exact previous registry values", async () =>
    {
        var before = Snapshot(); var environment = new FakeEnvironment(before) { FailNextPathWrite = true }; var settings = new AppSettings { LastSelectedId = "old" };
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), settings);
        True(!result.Success); True(result.RollbackComplete); Equal(before, environment.State); Equal("old", settings.LastSelectedId);
    });
    await Check("Rollback preserves concurrent external changes", async () =>
    {
        var environment = new FakeEnvironment(Snapshot()) { FailNextPathWrite = true, ExternalChange = true };
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), new AppSettings());
        True(!result.Success); True(!result.RollbackComplete); Equal(@"C:\ExternalJava", environment.State.UserJavaHome.Value);
    });
    await Check("Configuration save failure rolls back environment and selection", async () =>
    {
        var occupied = Path.Combine(temporaryRoot, "occupied"); await File.WriteAllTextAsync(occupied, "file"); var before = Snapshot(); var environment = new FakeEnvironment(before);
        var settings = new AppSettings { LastSelectedId = "old", LastSelectedFingerprint = "old-fingerprint" };
        var result = await new EnvironmentSwitchService(new ConfigStore(occupied), environment, new FakeProbe()).SwitchAsync(Target(), settings);
        True(!result.Success); True(result.RollbackComplete); Equal(before, environment.State); Equal("old", settings.LastSelectedId); Equal("old-fingerprint", settings.LastSelectedFingerprint); Equal(0, settings.Installations.Count);
    });
    await Check("Invalid installation performs zero writes", async () =>
    {
        var environment = new FakeEnvironment(Snapshot()); var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe { Valid = false }).SwitchAsync(Target(), new AppSettings());
        True(!result.Success); Equal(0, environment.Writes);
    });
    await Check("Recovery fingerprint mismatch performs zero writes", async () =>
    {
        var before = Snapshot(); var environment = new FakeEnvironment(before); var settings = new AppSettings { LastSelectedId = "old", LastSelectedFingerprint = "old-fingerprint" };
        var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(Target(), settings, expectedFingerprint: "old-fingerprint");
        True(!result.Success); Equal(0, environment.Writes); Equal(0, environment.Broadcasts); Equal(before, environment.State); Equal("old-fingerprint", settings.LastSelectedFingerprint);
    });
    await Check("Installation replacement during verification rolls back writes", async () =>
    {
        var before = Snapshot(); var environment = new FakeEnvironment(before); var result = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe { ChangeFingerprint = true }).SwitchAsync(Target(), new AppSettings());
        True(!result.Success); True(result.RollbackComplete); Equal(before, environment.State);
    });
    await Check("Repair plan removes only conflicting known Java entries and preserves original PATH text", () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath");
        var before = Snapshot() with { MachinePath = new($"C:\\Tools; \"{oracle}\" ;%JAVA_HOME%\\bin;;C:\\After", true, true) };
        var environment = new FakeEnvironment(before); environment.Files.Add(Path.Combine(oracle, "java.exe")); environment.Files.Add(@"C:\MachineJava\bin\java.exe");
        var plan = PathRepairPlanner.Create(before, Target(), null, environment.FileExists);
        True(plan.CanRepair); True(plan.RequiresElevation); True(plan.AffectsOtherUsers); Equal(2, plan.RemovedEntries.Count);
        Equal(@"C:\Tools;;C:\After", plan.ProposedMachinePath.Value); True(plan.ProposedMachinePath.Expandable);
        Equal(before.MachinePath, plan.OriginalMachinePath); Equal(before.MachineJavaHome, plan.ExpectedMachineJavaHome); Equal(0, environment.Writes);
        True(PathRepairPlanner.Validate(plan, before, "stable", environment.FileExists).Valid);
        True(PathRepairPlanner.CanRollbackMachinePath(plan, plan.ProposedMachinePath)); True(!PathRepairPlanner.CanRollbackMachinePath(plan, new(@"C:\ExternalChange")));
        return Task.CompletedTask;
    });
    await Check("Repair leaves Java entries after the selected machine installation unchanged", () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "java8path");
        var before = Snapshot() with { MachinePath = new($"{oracle};C:\\NewJava\\bin;{oracle};C:\\Tools") };
        var environment = new FakeEnvironment(before); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var plan = PathRepairPlanner.Create(before, Target(), null, environment.FileExists);
        True(plan.CanRepair); Equal(1, plan.RemovedEntries.Count); Equal($"C:\\NewJava\\bin;{oracle};C:\\Tools", plan.ProposedMachinePath.Value);
        return Task.CompletedTask;
    });
    await Check("Repair refuses unrelated Java installations and lookalike Oracle paths", () =>
    {
        var before = Snapshot() with { MachinePath = new(@"C:\OtherJava\bin;C:\Oracle\java8path") };
        var environment = new FakeEnvironment(before); environment.Files.Add(@"C:\OtherJava\bin\java.exe"); environment.Files.Add(@"C:\Oracle\java8path\java.exe");
        True(!PathRepairPlanner.Create(before, Target(), null, environment.FileExists).CanRepair);
        True(!PathRepairPlanner.Create(before with { MachinePath = new(@"C:\Oracle\java8path") }, Target(), null, environment.FileExists).CanRepair);
        Equal(0, environment.Writes); return Task.CompletedTask;
    });
    await Check("Repair validation rejects stale tampered and wrong-target plans", () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath");
        var before = Snapshot() with { MachinePath = new($"{oracle};C:\\Tools") };
        var environment = new FakeEnvironment(before); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var plan = PathRepairPlanner.Create(before, Target(), null, environment.FileExists); True(plan.CanRepair);
        True(!PathRepairPlanner.Validate(plan, before, "changed", environment.FileExists).Valid);
        True(!PathRepairPlanner.ValidateMachineRepair(plan, before with { MachinePath = new(@"C:\ExternalChange") }, environment.FileExists).Valid);
        True(!PathRepairPlanner.ValidateMachineRepair(plan with { ProposedMachinePath = new(@"C:\Injected") }, before, environment.FileExists).Valid);
        True(!PathRepairPlanner.ValidateMachineRepair(plan with { RemovedEntries = [] }, before, environment.FileExists).Valid);
        True(!PathRepairPlanner.ValidateMachineRepair(plan with { ProposedMachinePath = plan.ProposedMachinePath with { Expandable = true } }, before, environment.FileExists).Valid);
        True(!PathRepairPlanner.Validate(plan, before with { UserPath = new(@"C:\ChangedUser") }, "stable", environment.FileExists).Valid);
        return Task.CompletedTask;
    });
    await Check("Administrator helper validation permits a different user without trusting its HKCU", () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath");
        var before = Snapshot() with { MachinePath = new($"{oracle};C:\\Tools") };
        var environment = new FakeEnvironment(before); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var plan = PathRepairPlanner.Create(before, Target(), null, environment.FileExists);
        var administrator = before with { UserJavaHome = new(@"C:\AdminJava"), UserPath = new(@"C:\AdminTools") };
        True(PathRepairPlanner.ValidateMachineRepair(plan, administrator, environment.FileExists).Valid);
        True(PathRepairPlanner.ValidateMachineRepair(plan, before.MachineJavaHome, before.MachinePath, environment.FileExists).Valid);
        return Task.CompletedTask;
    });
    await Check("Repair rejects network device and alternate-stream paths before probing them", () =>
    {
        int probes = 0;
        foreach (var path in new[] { @"\\untrusted\share\jdk", @"\\?\C:\Java", @"C:\Java:stream", @"\\.\GLOBALROOT\Device\HarddiskVolume1\Java" })
        {
            var target = Target(); target.Path = path;
            True(!PathRepairPlanner.Create(Snapshot(), target, null, _ => { probes++; return true; }).CanRepair);
            True(!PathRepairPlanner.IsLocalFileSystemPath(path));
        }
        Equal(0, probes);
        True(PathRepairPlanner.IsLocalFileSystemPath(@"C:\Users\test\repair.json"));
        var snapshot = Snapshot() with { MachinePath = new(@"\\untrusted\share;C:\Tools") };
        True(!PathRepairPlanner.Create(snapshot, Target(), null, path => { if (path.StartsWith(@"\\")) throw new Exception("Unexpected network access."); return true; }).CanRepair);
        return Task.CompletedTask;
    });
    await Check("Automatic repair suppresses the same background conflict but permits explicit retries", () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle + @";C:\Tools") }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var plan = PathRepairPlanner.Create(environment.State, Target(), null, environment.FileExists);
        var policy = new PathRepairAttemptPolicy();
        True(!policy.CanAttempt(plan, recovery: true, readOnly: true));
        True(policy.CanAttempt(plan, recovery: true)); policy.RecordAttempt(plan); True(!policy.CanAttempt(plan, recovery: true));
        var changed = PathRepairPlanner.Create(environment.State with { MachinePath = new(@"C:\Unrelated;" + oracle), UserPath = new(@"C:\NewTools") }, Target(), null, environment.FileExists);
        True(changed.CanRepair); True(!policy.CanAttempt(changed, recovery: true));
        True(policy.CanAttempt(plan, recovery: false)); True(!policy.CanAttempt(plan, recovery: true));
        var replacement = plan with { ExpectedFingerprint = "replacement" }; True(!policy.CanAttempt(replacement, recovery: true));
        policy.SelectionSucceeded(); True(policy.CanAttempt(plan, recovery: true));
        return Task.CompletedTask;
    });
    await Check("Declining repair for A suppresses B recovery but permits a manual B retry", async () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle + @";%JAVA_HOME%\bin", true, true) }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var targetA = Target(); var targetB = Target(); targetB.Id = "protected"; targetB.Path = @"C:\MachineJava"; environment.Files.Add(Path.Combine(targetB.Path, "bin", "java.exe"));
        var settings = new AppSettings { LastSelectedId = targetB.Id, LastSelectedFingerprint = targetB.Fingerprint, Installations = [targetA, targetB] };
        var switcher = new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()); var policy = new PathRepairAttemptPolicy();
        var initialA = await switcher.SwitchAsync(targetA, settings); var planA = PathRepairPlanner.Create(environment.State, targetA, null, environment.FileExists);
        int repairs = 0, retries = 0;
        var declined = await PathRepairWorkflow.RunAsync(planA, initialA, policy, false, false,
            (_, _, started) => { repairs++; started(); return Task.FromResult(false); },
            (_, _) => throw new Exception("Cancelled repair must not retry."));
        True(declined.Cancelled); Equal(targetB.Id, settings.LastSelectedId); Equal(0, environment.Writes);
        var initialB = await switcher.SwitchAsync(targetB, settings, expectedFingerprint: targetB.Fingerprint);
        var planB = PathRepairPlanner.Create(environment.State, targetB, null, environment.FileExists);
        Equal(2, planA.RemovedEntries.Count); Equal(1, planB.RemovedEntries.Count);
        var suppressed = await PathRepairWorkflow.RunAsync(planB, initialB, policy, true, false,
            (_, _, _) => throw new Exception("The same conflict must not raise UAC for another target."),
            (_, _) => throw new Exception("Suppressed repair must not retry."));
        True(!suppressed.Attempted); Equal(1, repairs); Equal(0, environment.Writes);
        var manual = await PathRepairWorkflow.RunAsync(planB, initialB, policy, false, false,
            (_, _, started) => { repairs++; started(); environment = new FakeEnvironment(environment.State with { MachinePath = planB.ProposedMachinePath }); environment.Files.Add(Path.Combine(targetB.Path, "bin", "java.exe")); return Task.FromResult(true); },
            async (fingerprint, token) => { retries++; return await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(targetB, settings, token, fingerprint); });
        True(manual.Result.Success); Equal(2, repairs); Equal(1, retries); Equal(targetB.Path, environment.State.UserJavaHome.Value);
        policy.SelectionSucceeded(); True(policy.CanAttempt(planA, recovery: true)); True(policy.CanAttempt(planB, recovery: true));
    });
    await Check("Successful repair retries once with the verified fingerprint even during shutdown", async () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle) }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var settings = new AppSettings(); var target = Target(); var policy = new PathRepairAttemptPolicy();
        var initial = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, settings);
        var plan = PathRepairPlanner.Create(environment.State, target, null, environment.FileExists);
        True(!initial.Success); Equal(0, environment.Writes); int repairs = 0, retries = 0; using var shutdown = new CancellationTokenSource();
        var attempt = await PathRepairWorkflow.RunAsync(plan, initial, policy, recovery: true, readOnly: false,
            (_, token, started) => { repairs++; token.ThrowIfCancellationRequested(); started(); environment = new FakeEnvironment(environment.State with { MachinePath = plan.ProposedMachinePath }); shutdown.Cancel(); return Task.FromResult(true); },
            async (fingerprint, token) => { retries++; Equal("stable", fingerprint); True(!token.CanBeCanceled); return await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, settings, token, fingerprint); }, shutdown.Token);
        True(attempt.Result.Success); True(attempt.Attempted); True(!attempt.Cancelled); Equal(1, repairs); Equal(1, retries); Equal("new", settings.LastSelectedId); True(environment.Writes > 0);
    });
    await Check("Cancelled or failed elevation never retries selection or writes the user environment", async () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle) }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var target = Target(); var policy = new PathRepairAttemptPolicy();
        var initial = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, new AppSettings());
        var plan = PathRepairPlanner.Create(environment.State, target, null, environment.FileExists); int repairs = 0, retries = 0;
        Task<bool> Cancel(PathRepairPlan _, CancellationToken __, Action started) { repairs++; started(); return Task.FromResult(false); }
        Task<EnvironmentSwitchResult> Retry(string _, CancellationToken __) { retries++; throw new Exception("Unexpected retry."); }
        var cancelled = await PathRepairWorkflow.RunAsync(plan, initial, policy, true, false, Cancel, Retry);
        True(cancelled.Cancelled); True(cancelled.Attempted);
        var suppressed = await PathRepairWorkflow.RunAsync(plan, initial, policy, true, false, Cancel, Retry);
        True(!suppressed.Attempted); Equal(1, repairs); Equal(0, retries); Equal(0, environment.Writes);
        await Throws<IOException>(() => PathRepairWorkflow.RunAsync(plan, initial, policy, false, false, (_, _, started) => { repairs++; started(); throw new IOException("Helper failure."); }, Retry));
        suppressed = await PathRepairWorkflow.RunAsync(plan, initial, policy, true, false, Cancel, Retry);
        True(!suppressed.Attempted); Equal(2, repairs); Equal(0, retries); Equal(0, environment.Writes);
    });
    await Check("Unsupported and read-only repairs never invoke the helper", async () =>
    {
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(@"C:\OtherJava\bin") }); environment.Files.Add(@"C:\OtherJava\bin\java.exe");
        var target = Target(); var initial = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, new AppSettings());
        var plan = PathRepairPlanner.Create(environment.State, target, null, environment.FileExists); True(!plan.CanRepair);
        Task<bool> Repair(PathRepairPlan _, CancellationToken __, Action ___) => throw new Exception("Unexpected helper.");
        Task<EnvironmentSwitchResult> Retry(string _, CancellationToken __) => throw new Exception("Unexpected retry.");
        var result = await PathRepairWorkflow.RunAsync(plan, initial, new(), false, false, Repair, Retry); True(!result.Attempted);
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path"); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        plan = PathRepairPlanner.Create(environment.State with { MachinePath = new(oracle) }, target, null, environment.FileExists); True(plan.CanRepair);
        result = await PathRepairWorkflow.RunAsync(plan, initial, new(), false, true, Repair, Retry); True(!result.Attempted); Equal(0, environment.Writes);
    });
    await Check("A conflict after repair is returned after exactly one retry", async () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle) }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var target = Target(); var initial = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, new AppSettings());
        var plan = PathRepairPlanner.Create(environment.State, target, null, environment.FileExists); var policy = new PathRepairAttemptPolicy(); int repairs = 0, retries = 0;
        var result = await PathRepairWorkflow.RunAsync(plan, initial, policy, true, false,
            (_, _, started) => { repairs++; started(); return Task.FromResult(true); },
            async (fingerprint, token) => { retries++; return await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, new AppSettings(), token, fingerprint); });
        True(!result.Result.Success); Equal(initial.ConflictPath, result.Result.ConflictPath); Equal(1, repairs); Equal(1, retries); Equal(0, environment.Writes); True(!policy.CanAttempt(plan, recovery: true));
    });
    await Check("Pre-elevation validation failures do not suppress a later stable installation", async () =>
    {
        var oracle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Oracle", "Java", "java8path");
        var environment = new FakeEnvironment(Snapshot() with { MachinePath = new(oracle) }); environment.Files.Add(Path.Combine(oracle, "java.exe"));
        var target = Target(); var initial = await new EnvironmentSwitchService(environmentStore: environment, javaProbe: new FakeProbe()).SwitchAsync(target, new AppSettings());
        var plan = PathRepairPlanner.Create(environment.State, target, null, environment.FileExists); var policy = new PathRepairAttemptPolicy();
        Task<EnvironmentSwitchResult> Retry(string _, CancellationToken __) => throw new Exception("Unexpected retry.");
        await Throws<IOException>(() => PathRepairWorkflow.RunAsync(plan, initial, policy, true, false, (_, _, _) => throw new IOException("Stale before UAC."), Retry));
        True(policy.CanAttempt(plan, recovery: true));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => PathRepairWorkflow.RunAsync(plan, initial, policy, true, false, (_, _, _) => throw new Exception("Unexpected helper."), Retry, cancelled.Token));
        True(policy.CanAttempt(plan, recovery: true)); Equal(0, environment.Writes);
    });
    if (OperatingSystem.IsWindows()) await Check("Real isolated child probe enforces timeout and two-process limit", async () =>
    {
        var home = Path.Combine(temporaryRoot, "fake-jdk"); var bin = Path.Combine(home, "bin"); Directory.CreateDirectory(bin);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(bin, Path.GetFileName(file) == "JavaSelect.Core.Tests.exe" ? "java.exe" : Path.GetFileName(file)));
        await File.WriteAllTextAsync(Path.Combine(bin, "javac.exe"), "fixture"); var service = new JavaDiscoveryService(TimeSpan.FromSeconds(3));
        var installation = await service.ProbeAsync(Path.Combine(bin, "java.exe")); True(installation?.IsValid == true); Equal(8, installation!.MajorVersion); Equal("Oracle", installation.Vendor); Equal("JDK", installation.Kind); Equal("x64", installation.Architecture); Equal(home, installation.Path);
        await File.WriteAllTextAsync(Path.Combine(bin, "concurrency.txt"), "0,0"); var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.ProbeAsync(home))); True(results.All(x => x?.IsValid == true));
        var counts = (await File.ReadAllTextAsync(Path.Combine(bin, "concurrency.txt"))).Split(',').Select(int.Parse).ToArray(); Equal(0, counts[0]); True(counts[1] > 0 && counts[1] <= 2);
        await File.WriteAllTextAsync(Path.Combine(bin, "timeout"), ""); installation = await new JavaDiscoveryService(TimeSpan.FromMilliseconds(150)).ProbeAsync(home); True(installation?.IsValid == false); True(installation!.Error!.Contains("timeout"));
    });
}
finally
{
    var resolved = Path.GetFullPath(temporaryRoot);
    if (Path.GetDirectoryName(resolved) == Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(resolved).StartsWith("JavaSelect-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}
Console.WriteLine($"{passed} passed, {failed} failed. No real environment variables were modified.");
Environment.ExitCode = failed == 0 ? 0 : 1;

async Task CreateJunctionAsync(string path, string target)
{
    var script = Path.Combine(temporaryRoot, "junction.ps1");
    await File.WriteAllTextAsync(script, "param([string]$JunctionPath,[string]$TargetPath)\nNew-Item -ItemType Junction -Path $JunctionPath -Target $TargetPath -ErrorAction Stop | Out-Null");
    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-JunctionPath", path, "-TargetPath", target }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!; await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException(await process.StandardError.ReadToEndAsync());
}

sealed class FakeProbe : IJavaProbe
{
    public bool Valid { get; set; } = true;
    public bool ChangeFingerprint { get; set; }
    private int calls;
    public Task<JavaInstallation?> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<JavaInstallation?>(new()
    { Path = path, Version = "21.0.11", MajorVersion = 21, Vendor = "Oracle", Kind = "JDK", IsValid = Valid, Fingerprint = ChangeFingerprint && ++calls > 1 ? "replaced" : "stable" });
}

sealed class FakeEnvironment(EnvironmentSnapshot state) : IEnvironmentStore
{
    public EnvironmentSnapshot State { get; private set; } = state;
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { @"C:\NewJava\bin\java.exe" };
    public bool FailNextPathWrite { get; set; }
    public bool ExternalChange { get; set; }
    public int Writes { get; private set; }
    public int Broadcasts { get; private set; }
    public Task<EnvironmentSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
    public bool FileExists(string path) => Files.Contains(path);
    public Task BroadcastAsync(CancellationToken cancellationToken = default) { Broadcasts++; return Task.CompletedTask; }
    public Task WriteUserValueAsync(string name, EnvironmentValue value, CancellationToken cancellationToken = default)
    {
        Writes++;
        if (name == "Path" && FailNextPathWrite)
        {
            FailNextPathWrite = false;
            if (ExternalChange) State = State with { UserJavaHome = new(@"C:\ExternalJava") };
            throw new IOException("Injected write failure.");
        }
        State = name == "Path" ? State with { UserPath = value } : State with { UserJavaHome = value };
        return Task.CompletedTask;
    }
}
