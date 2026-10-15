using System.Text.Json;
using System.Text.RegularExpressions;

namespace JavaSelect.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim imageGate = new(1, 1);
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public ConfigStore(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JavaSelect"));

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNoReparsePoints(FilePath);
            if (!File.Exists(FilePath))
            {
                var initial = new AppSettings();
                await SaveCoreAsync(initial, cancellationToken);
                return initial;
            }
            if (new FileInfo(FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("The settings file is too large.");
            await using var stream = File.OpenRead(FilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var settings = document.RootElement.Deserialize<AppSettings>(Options)
                ?? throw new InvalidDataException("The settings file is empty.");
            if (settings.SchemaVersion > 1) throw new InvalidDataException("This settings file requires a newer JavaSelect version.");
            MigrateLegacyCornerRadius(settings, document.RootElement);
            MigrateLegacyImageBlur(settings, document.RootElement);
            Normalize(settings);
            return settings;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (settings.SchemaVersion > 1) throw new InvalidDataException("This settings file requires a newer JavaSelect version.");
            Normalize(settings);
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken token)
    {
        EnsureNoReparsePoints(FilePath);
        Directory.CreateDirectory(DirectoryPath);
        EnsureNoReparsePoints(FilePath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                var document = JsonSerializer.SerializeToNode(settings, Options)!;
                var image = ResolveBackgroundPath(settings.OverlayImagePath);
                document[nameof(AppSettings.OverlayImagePath)] = image is null ? null : Path.GetRelativePath(DirectoryPath, image);
                await JsonSerializer.SerializeAsync(stream, document, Options, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(FilePath);
            EnsureNoReparsePoints(temporary);
            if (File.Exists(FilePath)) await ReplaceAsync(temporary, token);
            else File.Move(temporary, FilePath);
        }
        finally
        {
            EnsureNoReparsePoints(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Antivirus scanners and indexers briefly lock a settings file, which makes File.Replace fail with "unable to remove the
    // file to be replaced"; a short bounded retry rides that out and still reports a lock that does not go away.
    private async Task ReplaceAsync(string temporary, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Replace(temporary, FilePath, null); return; }
            catch (IOException error) when (attempt < 6 && error is not (FileNotFoundException or DirectoryNotFoundException))
            {
                await Task.Delay(50 * attempt, token);
            }
        }
    }

    public async Task<string> ImportBackgroundAsync(string sourcePath, AppSettings settings,
        Func<string, CancellationToken, Task> validateCopiedImage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateCopiedImage);
        await imageGate.WaitAsync(cancellationToken);
        try
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists || source.Length == 0 || source.Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Choose an image smaller than 20 MB.");
            var extension = source.Extension.ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new InvalidDataException("Choose a PNG, JPEG, or WebP image.");
            var directory = Path.Combine(DirectoryPath, "backgrounds");
            EnsureNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            EnsureNoReparsePoints(directory);
            var destination = Path.Combine(directory, "background-" + Guid.NewGuid().ToString("N") + extension);
            var previousPath = settings.OverlayImagePath;
            var previousName = settings.OverlayImageName;
            var previousStyle = settings.OverlayBackgroundStyle;
            try
            {
                EnsureNoReparsePoints(destination);
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                {
                    if (input.Length == 0 || input.Length > 20 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 20 MB.");
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(true);
                }
                EnsureNoReparsePoints(destination);
                await validateCopiedImage(destination, cancellationToken);
                EnsureNoReparsePoints(destination);
                cancellationToken.ThrowIfCancellationRequested();
                settings.OverlayImagePath = destination;
                settings.OverlayImageName = source.Name;
                settings.OverlayBackgroundStyle = "image";
                await SaveAsync(settings, cancellationToken);
            }
            catch
            {
                settings.OverlayImagePath = previousPath;
                settings.OverlayImageName = previousName;
                settings.OverlayBackgroundStyle = previousStyle;
                DeleteOwnedImage(destination);
                throw;
            }
            DeleteOwnedImage(previousPath);
            return destination;
        }
        finally { imageGate.Release(); }
    }

    public async Task RemoveBackgroundAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await imageGate.WaitAsync(cancellationToken);
        try
        {
            var oldPath = settings.OverlayImagePath; var oldName = settings.OverlayImageName; var oldStyle = settings.OverlayBackgroundStyle;
            settings.OverlayImagePath = null; settings.OverlayImageName = null;
            if (settings.OverlayBackgroundStyle == "image") settings.OverlayBackgroundStyle = "frosted";
            try { await SaveAsync(settings, cancellationToken); }
            catch { settings.OverlayImagePath = oldPath; settings.OverlayImageName = oldName; settings.OverlayBackgroundStyle = oldStyle; throw; }
            DeleteOwnedImage(oldPath);
        }
        finally { imageGate.Release(); }
    }

    public string? ResolveBackgroundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var resolved = Path.GetFullPath(path, DirectoryPath);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.Combine(DirectoryPath, "backgrounds"), StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(Path.GetFileName(resolved), @"^background-[0-9a-f]{32}\.(png|jpe?g|webp)$")) return null;
            EnsureNoReparsePoints(resolved);
            return File.Exists(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private void DeleteOwnedImage(string? path)
    {
        try { if (ResolveBackgroundPath(path) is { } resolved) File.Delete(resolved); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The configuration path must not contain symbolic links or directory junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void MigrateLegacyCornerRadius(AppSettings settings, JsonElement document)
    {
        JsonElement legacy = default;
        foreach (var property in document.EnumerateObject())
        {
            if (property.Name.Equals(nameof(AppSettings.OverlayCornerRadius), StringComparison.OrdinalIgnoreCase)) return;
            if (property.Name.Equals("OverlayCss", StringComparison.OrdinalIgnoreCase)) legacy = property.Value;
        }
        if (legacy.ValueKind != JsonValueKind.String || legacy.GetString() is not { Length: <= 4096 } css) return;
        var rule = Regex.Match(css, @"^\s*\.switcher\s*\{([^{}]*)\}\s*$", RegexOptions.Singleline);
        if (!rule.Success) return;
        int? radius = null;
        foreach (var declaration in rule.Groups[1].Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0) return;
            if (declaration[..colon].Trim() != "--radius") continue;
            var value = declaration[(colon + 1)..].Trim();
            if (!Regex.IsMatch(value, @"^[0-9]{1,2}px$") || !int.TryParse(value[..^2], out var number) || number > 40) return;
            radius = number;
        }
        if (radius is { } migrated) settings.OverlayCornerRadius = migrated;
    }

    private static void MigrateLegacyImageBlur(AppSettings settings, JsonElement document)
    {
        if (settings.OverlayBackgroundStyle != "image") return;
        JsonElement blur = default;
        foreach (var property in document.EnumerateObject())
        {
            if (property.Name.Equals(nameof(AppSettings.OverlayImageBlurEnabled), StringComparison.OrdinalIgnoreCase)) return;
            if (property.Name.Equals(nameof(AppSettings.OverlayImageBlur), StringComparison.OrdinalIgnoreCase)) blur = property.Value;
        }
        if (blur.ValueKind == JsonValueKind.Number && blur.TryGetDouble(out var radius) && double.IsFinite(radius) && radius > 0)
            settings.OverlayImageBlurEnabled = true;
    }

    private void Normalize(AppSettings settings)
    {
        settings.Language = LocaleResolver.Resolve(settings.Language);
        if (settings.Theme is not ("system" or "light" or "dark")) settings.Theme = "system";
        if (settings.SwitchMode is not ("instant" or "confirm")) settings.SwitchMode = "confirm";
        settings.SortMode = InstallationOrder.Normalize(settings.SortMode);
        settings.OverlayMonitor = LauncherMonitorChoice.Normalize(settings.OverlayMonitor);
        if (settings.OverlayBackgroundStyle is not ("frosted" or "solid" or "image")) settings.OverlayBackgroundStyle = "frosted";
        if (settings.OverlayTextColor is not ("auto" or "black" or "white")) settings.OverlayTextColor = "auto";
        if (settings.OverlaySelectionColor != "auto" && !Regex.IsMatch(settings.OverlaySelectionColor ?? "", "\\A#[0-9a-fA-F]{6}\\z")) settings.OverlaySelectionColor = "auto";
        if (settings.OverlaySelectionBackgroundColor != "auto" && !Regex.IsMatch(settings.OverlaySelectionBackgroundColor ?? "", "\\A#[0-9a-fA-F]{6}\\z")) settings.OverlaySelectionBackgroundColor = "auto";
        settings.OverlayOpacity = double.IsFinite(settings.OverlayOpacity) ? Math.Clamp(settings.OverlayOpacity, 20, 100) : 94;
        settings.OverlayCornerRadius = double.IsFinite(settings.OverlayCornerRadius) ? Math.Clamp(settings.OverlayCornerRadius, 0, 40) : 10;
        settings.OverlayImageBlur = double.IsFinite(settings.OverlayImageBlur) ? Math.Clamp(settings.OverlayImageBlur, 0, 30) : 5;
        settings.OverlayTextBackgroundOpacity = double.IsFinite(settings.OverlayTextBackgroundOpacity) ? Math.Clamp(settings.OverlayTextBackgroundOpacity, 0, 100) : 100;
        settings.OverlayTextBackgroundCornerRadius = double.IsFinite(settings.OverlayTextBackgroundCornerRadius) ? Math.Clamp(settings.OverlayTextBackgroundCornerRadius, 0, 40) : 10;
        if (!Regex.IsMatch(settings.OverlayTextBackgroundColor ?? "", "\\A#[0-9a-fA-F]{6}\\z")) settings.OverlayTextBackgroundColor = "#FFFFFF";
        if (!Regex.IsMatch(settings.OverlaySolidColor ?? "", "^#[0-9a-fA-F]{6}$")) settings.OverlaySolidColor = "#f6f7fb";
        settings.OverlayImagePath = ResolveBackgroundPath(settings.OverlayImagePath);
        if (settings.OverlayImagePath is null) { settings.OverlayImageName = null; if (settings.OverlayBackgroundStyle == "image") settings.OverlayBackgroundStyle = "frosted"; }
        settings.Installations = (settings.Installations ?? []).Where(x => x is not null).ToList();
        foreach (var installation in settings.Installations)
        {
            if (string.IsNullOrWhiteSpace(installation.Id)) installation.Id = Guid.NewGuid().ToString("N");
            installation.Path ??= ""; installation.Alias ??= ""; installation.Version ??= ""; installation.Vendor ??= "";
            installation.Architecture ??= ""; installation.Hotkey ??= ""; installation.Fingerprint ??= "";
            if (installation.Kind is not ("JDK" or "JRE")) installation.Kind = "JRE";
            if (string.IsNullOrWhiteSpace(installation.Path)) installation.IsValid = false;
        }
        if (!HotkeyGesture.TryParse(settings.OverlayHotkey, out _)) settings.OverlayHotkey = "Alt+J";
    }
}
