using System.Globalization;
using System.Text.Json.Serialization;

namespace JavaSelect.Core;

public static class ProjectInfo
{
    public const string Repository = "xiaoranawa/javaselect";
    public const string RepositoryUrl = "https://github.com/" + Repository;
}

public sealed class JavaInstallation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Version { get; set; } = "";
    public int MajorVersion { get; set; }
    public string Vendor { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Kind { get; set; } = "JRE";
    public string Hotkey { get; set; } = "";
    public bool ShowInLauncher { get; set; } = true;
    public bool IsValid { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? IconPath { get; set; }
    public string? Error { get; set; }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? $"Java {MajorVersion} · {Kind}" : Alias;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Language { get; set; } = LocaleResolver.Resolve(CultureInfo.CurrentUICulture.Name);
    public string Theme { get; set; } = "system";
    public string OverlayHotkey { get; set; } = "Alt+J";
    public string SwitchMode { get; set; } = "confirm";
    public bool WrapSelection { get; set; } = true;
    public bool StartAtLogin { get; set; }
    public bool StartInTray { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutomaticTerminalSync { get; set; }
    public bool KeepCurrentJavaSelection { get; set; } = true;
    public bool SwitchNotifications { get; set; } = true;
    public bool DiscoveryNotifications { get; set; } = true;
    public bool RestoreNotifications { get; set; } = true;
    public string OverlayBackgroundStyle { get; set; } = "frosted";
    public double OverlayOpacity { get; set; } = 94;
    public string OverlaySolidColor { get; set; } = "#f6f7fb";
    public string OverlayTextColor { get; set; } = "auto";
    public string? OverlayImagePath { get; set; }
    public string? OverlayImageName { get; set; }
    public double OverlayCornerRadius { get; set; } = 10;
    public string? LastSelectedId { get; set; }
    public string? LastSelectedFingerprint { get; set; }
    public string? ManagedPathEntry { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public List<JavaInstallation> Installations { get; set; } = [];
}

public sealed record EnvironmentValue(string? Value, bool Exists = true, bool Expandable = false)
{
    public static EnvironmentValue Missing { get; } = new(null, false);
}

public sealed record EnvironmentSnapshot(EnvironmentValue UserJavaHome, EnvironmentValue UserPath,
    EnvironmentValue MachineJavaHome, EnvironmentValue MachinePath,
    IReadOnlyDictionary<string, string>? OtherVariables = null,
    IReadOnlyDictionary<string, EnvironmentValue>? MachineVariables = null,
    IReadOnlyDictionary<string, EnvironmentValue>? UserVariables = null);

public sealed record EnvironmentSwitchResult(bool Success, string? Error = null, string? ConflictPath = null,
    JavaInstallation? Installation = null, string? PreviousJavaHome = null, bool RollbackComplete = true);

public interface IJavaProbe
{
    Task<JavaInstallation?> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IEnvironmentStore
{
    Task<EnvironmentSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteUserValueAsync(string name, EnvironmentValue value, CancellationToken cancellationToken = default);
    bool FileExists(string path);
    Task BroadcastAsync(CancellationToken cancellationToken = default);
}
