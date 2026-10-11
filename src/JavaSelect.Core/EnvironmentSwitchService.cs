using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JavaSelect.Core;

public sealed class EnvironmentSwitchService
{
    private readonly ConfigStore? configStore;
    private readonly IEnvironmentStore environmentStore;
    private readonly IJavaProbe javaProbe;
    private readonly SemaphoreSlim gate = new(1, 1);

    public EnvironmentSwitchService(ConfigStore? configStore = null, IEnvironmentStore? environmentStore = null, IJavaProbe? javaProbe = null)
    {
        this.configStore = configStore;
        this.environmentStore = environmentStore ?? new WindowsEnvironmentStore();
        this.javaProbe = javaProbe ?? new JavaDiscoveryService();
    }

    public async Task<EnvironmentSwitchResult> SwitchAsync(JavaInstallation installation, AppSettings settings,
        CancellationToken cancellationToken = default, string? expectedFingerprint = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var verified = await javaProbe.ProbeAsync(installation.Path, cancellationToken);
            if (verified?.IsValid != true) return new(false, verified?.Error ?? "The Java installation is unavailable.");
            if (!string.IsNullOrEmpty(expectedFingerprint) && verified.Fingerprint != expectedFingerprint)
                return new(false, "The selected Java installation changed after the last successful switch. Select it manually to confirm the new installation.");
            verified.Id = installation.Id; verified.Alias = installation.Alias; verified.Hotkey = installation.Hotkey; verified.IconPath = installation.IconPath;
            verified.ShowInLauncher = installation.ShowInLauncher;
            var snapshot = await environmentStore.ReadAsync(cancellationToken);
            var targetHome = new EnvironmentValue(verified.Path);
            var targetBin = Path.Combine(verified.Path, "bin");
            var plan = PlanUserPath(snapshot.UserPath.Value, settings.ManagedPathEntry, targetBin);
            var targetPath = new EnvironmentValue(plan.Path, true, snapshot.UserPath.Expandable || plan.Path.Contains('%'));
            var proposed = snapshot with { UserJavaHome = targetHome, UserPath = targetPath };
            var firstJava = ResolveFirstJava(proposed, environmentStore.FileExists);
            var targetJava = Path.Combine(targetBin, "java.exe");
            if (!EquivalentPath(firstJava, targetJava))
                return new(false, "A Java entry in the system PATH takes precedence. Update the system PATH, then try again.", firstJava, verified, snapshot.UserJavaHome.Value);

            var previousId = settings.LastSelectedId;
            var previousFingerprint = settings.LastSelectedFingerprint;
            var previousManaged = settings.ManagedPathEntry;
            var previousInstallations = settings.Installations.ToList();
            bool homeWritten = false, pathWritten = false;
            try
            {
                if (snapshot.UserJavaHome != targetHome)
                {
                    var current = await environmentStore.ReadAsync(cancellationToken);
                    if (current.UserJavaHome != snapshot.UserJavaHome || current.UserPath != snapshot.UserPath)
                        throw new IOException("The user environment changed before the switch. Try again after the Java installer finishes.");
                    homeWritten = true;
                    await environmentStore.WriteUserValueAsync("JAVA_HOME", targetHome, cancellationToken);
                }
                if (snapshot.UserPath != targetPath)
                {
                    var current = await environmentStore.ReadAsync(cancellationToken);
                    if (current.UserPath != snapshot.UserPath || current.UserJavaHome != targetHome)
                        throw new IOException("The user environment changed during the switch. Try again after the Java installer finishes.");
                    pathWritten = true;
                    await environmentStore.WriteUserValueAsync("Path", targetPath, cancellationToken);
                }
                await VerifyEnvironmentAsync();
                var finalProbe = await javaProbe.ProbeAsync(verified.Path, cancellationToken);
                if (finalProbe?.IsValid != true || finalProbe.Fingerprint != verified.Fingerprint)
                    throw new IOException("The selected Java installation changed during the switch.");
                await VerifyEnvironmentAsync();
                settings.LastSelectedId = verified.Id;
                settings.LastSelectedFingerprint = verified.Fingerprint;
                settings.ManagedPathEntry = plan.OwnsEntry ? targetBin : null;
                settings.Installations = previousInstallations.Select(x => x.Id == verified.Id ? verified : x).ToList();
                if (!settings.Installations.Any(x => x.Id == verified.Id)) settings.Installations.Add(verified);
                if (configStore is not null) await configStore.SaveAsync(settings, cancellationToken);
                try { await environmentStore.BroadcastAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                return new(true, Installation: verified, PreviousJavaHome: snapshot.UserJavaHome.Value);

                async Task VerifyEnvironmentAsync()
                {
                    var current = await environmentStore.ReadAsync(cancellationToken);
                    if (current.UserJavaHome != targetHome || current.UserPath != targetPath
                        || !EquivalentPath(ResolveFirstJava(current, environmentStore.FileExists), targetJava))
                        throw new IOException("The environment changed during verification. Try again after the Java installer finishes.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or System.Security.SecurityException)
            {
                settings.LastSelectedId = previousId; settings.LastSelectedFingerprint = previousFingerprint;
                settings.ManagedPathEntry = previousManaged; settings.Installations = previousInstallations;
                var complete = await RollbackAsync(snapshot, targetHome, targetPath, homeWritten, pathWritten);
                return new(false, ex is OperationCanceledException ? "The switch was cancelled." : ex.Message,
                    Installation: verified, PreviousJavaHome: snapshot.UserJavaHome.Value, RollbackComplete: complete);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(false, ex.Message); }
        finally { gate.Release(); }
    }

    private async Task<bool> RollbackAsync(EnvironmentSnapshot snapshot, EnvironmentValue home, EnvironmentValue path, bool homeWritten, bool pathWritten)
    {
        var complete = true;
        if (pathWritten)
        {
            try
            {
                var current = await environmentStore.ReadAsync();
                if (current.UserPath == path) await environmentStore.WriteUserValueAsync("Path", snapshot.UserPath);
                else if (current.UserPath != snapshot.UserPath) complete = false;
            }
            catch { complete = false; }
        }
        if (homeWritten)
        {
            try
            {
                var current = await environmentStore.ReadAsync();
                if (current.UserJavaHome == home) await environmentStore.WriteUserValueAsync("JAVA_HOME", snapshot.UserJavaHome);
                else if (current.UserJavaHome != snapshot.UserJavaHome) complete = false;
            }
            catch { complete = false; }
        }
        try { await environmentStore.BroadcastAsync(); } catch { complete = false; }
        return complete;
    }

    public async Task<bool> IsCurrentAsync(JavaInstallation installation, CancellationToken cancellationToken = default)
    {
        var current = await environmentStore.ReadAsync(cancellationToken);
        var home = current.UserJavaHome.Exists ? current.UserJavaHome.Value : current.MachineJavaHome.Value;
        return EquivalentPath(home, installation.Path) && EquivalentPath(ResolveFirstJava(current, environmentStore.FileExists), Path.Combine(installation.Path, "bin", "java.exe"));
    }

    public static string BuildUserPath(string? currentPath, string? previousManagedEntry, string targetBin) => PlanUserPath(currentPath, previousManagedEntry, targetBin).Path;
    private static (string Path, bool OwnsEntry) PlanUserPath(string? currentPath, string? previousManagedEntry, string targetBin)
    {
        var segments = (currentPath ?? "").Split(';').ToList();
        if (!string.IsNullOrWhiteSpace(previousManagedEntry))
        {
            var index = segments.FindIndex(x => EquivalentPath(x, previousManagedEntry));
            if (index >= 0) segments.RemoveAt(index);
        }
        if (segments.Count > 0 && EquivalentPath(segments[0], targetBin)) return (string.Join(';', segments), false);
        var remaining = string.Join(';', segments);
        return (targetBin + (remaining.Length > 0 ? ";" + remaining : ""), true);
    }

    public static string? ResolveFirstJava(EnvironmentSnapshot snapshot, Func<string, bool> exists)
    {
        foreach (var machine in new[] { true, false })
        foreach (var expanded in ExpandPathEntries(snapshot, machine))
        {
            if (!Path.IsPathFullyQualified(expanded) || expanded.Contains('%')) continue;
            try { var candidate = Path.Combine(Path.GetFullPath(expanded), "java.exe"); if (exists(candidate)) return candidate; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }

    internal static IEnumerable<string> ExpandPathEntries(EnvironmentSnapshot snapshot, bool machine)
    {
        var variables = ScopeVariables(snapshot, machine);
        var value = machine ? snapshot.MachinePath : snapshot.UserPath;
        foreach (var entry in (value.Value ?? "").Split(';'))
            yield return (value.Expandable ? Expand(entry, variables) : entry).Trim().Trim('"');
    }

    private static Dictionary<string, string> ScopeVariables(EnvironmentSnapshot snapshot, bool machine)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "ALLUSERSPROFILE", "ProgramData", "PUBLIC", "SystemDrive", "SystemRoot" })
            if (snapshot.OtherVariables?.TryGetValue(name, out var value) == true) variables[name] = value;
        ApplyScope(snapshot.MachineVariables, snapshot.MachineJavaHome);
        if (!machine)
        {
            foreach (var name in new[] { "APPDATA", "COMPUTERNAME", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE", "USERNAME", "USERDOMAIN" })
                if (snapshot.OtherVariables?.TryGetValue(name, out var value) == true) variables[name] = value;
            ApplyScope(snapshot.UserVariables, snapshot.UserJavaHome);
        }
        return variables;

        void ApplyScope(IReadOnlyDictionary<string, EnvironmentValue>? stored, EnvironmentValue javaHome)
        {
            var values = stored is null ? new Dictionary<string, EnvironmentValue>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, EnvironmentValue>(stored, StringComparer.OrdinalIgnoreCase);
            if (javaHome.Exists) values["JAVA_HOME"] = javaHome; else values.Remove("JAVA_HOME");
            foreach (var pair in values.Where(x => x.Value.Exists && !x.Value.Expandable && !x.Key.Equals("Path", StringComparison.OrdinalIgnoreCase))) variables[pair.Key] = pair.Value.Value ?? "";
            var previous = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values.Where(x => x.Value.Exists && x.Value.Expandable && !x.Key.Equals("Path", StringComparison.OrdinalIgnoreCase))) variables[pair.Key] = Expand(pair.Value.Value ?? "", previous);
        }
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var next = Regex.Replace(value, "%([^%]+)%", match => variables.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value);
            if (next == value) break;
            value = next;
        }
        return value;
    }

    public static bool EquivalentPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim().Trim('"'))), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Trim().Trim('"'))), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}

public sealed class WindowsEnvironmentStore : IEnvironmentStore
{
    private static readonly SemaphoreSlim BroadcastGate = new(1, 1);
    public Task<EnvironmentSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        return Task.FromResult(ReadWindows());
    }

    [SupportedOSPlatform("windows")]
    private static EnvironmentSnapshot ReadWindows()
    {
        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()) variables[(string)item.Key] = (string?)item.Value ?? "";
        var machineValues = ReadValues(machine); var userValues = ReadValues(user);
        return new(ReadValue(user, "JAVA_HOME"), ReadValue(user, "Path"), ReadValue(machine, "JAVA_HOME"), ReadValue(machine, "Path"), variables, machineValues, userValues);

        static IReadOnlyDictionary<string, EnvironmentValue> ReadValues(RegistryKey? key)
        {
            var values = new Dictionary<string, EnvironmentValue>(StringComparer.OrdinalIgnoreCase);
            if (key is not null) foreach (var name in key.GetValueNames())
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    values[name] = new(value, true, key.GetValueKind(name) == RegistryValueKind.ExpandString);
            return values;
        }
    }

    [SupportedOSPlatform("windows")]
    private static EnvironmentValue ReadValue(RegistryKey? key, string name)
    {
        if (key is null) return EnvironmentValue.Missing;
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return EnvironmentValue.Missing;
        if (raw is not string value) throw new InvalidDataException($"The {name} environment value has an unsupported registry format.");
        return new(value, true, key.GetValueKind(name) == RegistryValueKind.ExpandString);
    }

    public Task WriteUserValueAsync(string name, EnvironmentValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        WriteWindows(name, value); return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string name, EnvironmentValue value)
    {
        if (name is not ("JAVA_HOME" or "Path")) throw new ArgumentOutOfRangeException(nameof(name));
        using var key = Registry.CurrentUser.CreateSubKey("Environment", true);
        if (value.Exists) key.SetValue(name, value.Value ?? "", value.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        else key.DeleteValue(name, false);
    }

    public bool FileExists(string path) => File.Exists(path);

    public async Task BroadcastAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !await BroadcastGate.WaitAsync(0, cancellationToken)) return;
        var broadcast = Task.Run(() =>
        {
            try { SendMessageTimeout(new nint(0xFFFF), 0x001A, 0, "Environment", 0x0002, 1500, out _); }
            finally { BroadcastGate.Release(); }
        }, CancellationToken.None);
        await Task.WhenAny(broadcast, Task.Delay(2000, cancellationToken));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
