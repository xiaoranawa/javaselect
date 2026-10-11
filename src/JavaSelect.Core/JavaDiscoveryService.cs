using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JavaSelect.Core;

public sealed class JavaDiscoveryService : IJavaProbe
{
    private readonly SemaphoreSlim probeGate = new(2, 2);
    private readonly TimeSpan probeTimeout;
    public JavaDiscoveryService(TimeSpan? probeTimeout = null) => this.probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5);

    public static string? NormalizeJavaHome(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    public async Task<JavaInstallation?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        await probeGate.WaitAsync(cancellationToken);
        try { return await ProbeCoreAsync(path, cancellationToken); }
        finally { probeGate.Release(); }
    }

    private async Task<JavaInstallation?> ProbeCoreAsync(string path, CancellationToken token)
    {
        var home = NormalizeJavaHome(path);
        if (home is null) return null;
        var result = new JavaInstallation { Path = home, Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.ToUpperInvariant())))[..24].ToLowerInvariant() };
        var executable = Path.Combine(home, "bin", "java.exe");
        if (!File.Exists(executable)) { result.Error = "java.exe was not found in the selected installation."; return result; }
        try
        {
            var release = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var releasePath = Path.Combine(home, "release");
            if (File.Exists(releasePath) && new FileInfo(releasePath).Length <= 128 * 1024)
                foreach (var line in await File.ReadAllLinesAsync(releasePath, token))
                {
                    var equals = line.IndexOf('=');
                    if (equals > 0) release[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
                }
            var execution = await ExecuteAsync(executable, home, token);
            if (execution.Error is not null) { result.Error = execution.Error; return result; }
            var properties = ParseProperties(execution.Output);
            result.Version = Get(properties, "java.version") ?? Get(release, "JAVA_VERSION") ?? ParseVersion(execution.Output) ?? "";
            result.MajorVersion = ParseMajorVersion(result.Version);
            result.Vendor = NormalizeVendor(Get(properties, "java.vendor") ?? Get(release, "IMPLEMENTOR") ?? "");
            result.Architecture = NormalizeArchitecture(Get(properties, "os.arch") ?? Get(release, "OS_ARCH") ?? "");
            result.Kind = File.Exists(Path.Combine(home, "bin", "javac.exe")) ? "JDK" : "JRE";
            var binary = new FileInfo(executable);
            result.Fingerprint = $"{binary.Length}:{binary.LastWriteTimeUtc.Ticks}:{result.Version}:{result.Vendor}";
            result.IsValid = result.MajorVersion > 0;
            if (!result.IsValid) result.Error = "Java did not report a valid version.";
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { result.Error = ex.Message; result.IsValid = false; return result; }
    }

    private async Task<(string Output, string? Error)> ExecuteAsync(string executable, string home, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(probeTimeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home } };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        foreach (var name in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" }) process.StartInfo.Environment.Remove(name);
        process.Start();
        var standardOutput = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var standardError = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput + "\n" + await standardError;
            return process.ExitCode == 0 ? (output, null) : (output, $"Java exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(standardOutput, standardError); } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            return ("", "Java did not respond within the timeout.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (output.Length < 128 * 1024) output.Append(buffer, 0, Math.Min(count, 128 * 1024 - output.Length));
        return output.ToString();
    }

    public async Task<IReadOnlyList<JavaInstallation>> ScanAsync(IEnumerable<string>? additionalRoots = null, CancellationToken cancellationToken = default)
    {
        var extra = additionalRoots?.ToArray() ?? [];
        var candidates = await Task.Run(() =>
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? value) { if (NormalizeJavaHome(value) is { } normalized) roots.Add(normalized); }
            Add(Environment.GetEnvironmentVariable("JAVA_HOME"));
            foreach (var value in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(value.Trim('"'), "java.exe"))) Add(value);
            foreach (var program in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0))
                foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Amazon Corretto", "Zulu", "BellSoft", "Semeru", "OpenJDK" }) Add(Path.Combine(program, vendor));
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add(Path.Combine(user, ".jdks")); Add(Path.Combine(user, "scoop", "apps"));
            foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                foreach (var directory in new[] { "JDK", "Java" }) Add(Path.Combine(drive.RootDirectory.FullName, directory));
            foreach (var value in extra) Add(value);
            if (OperatingSystem.IsWindows()) foreach (var value in RegistryJavaHomes()) Add(value);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in EnumerateInstallations(root, cancellationToken)) result.Add(candidate);
            }
            return result;
        }, cancellationToken);
        var installations = new ConcurrentBag<JavaInstallation>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (candidate, token) =>
        {
            if (await ProbeAsync(candidate, token) is { IsValid: true } installation) installations.Add(installation);
        });
        return installations.OrderByDescending(x => x.MajorVersion).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateInstallations(string root, CancellationToken token)
    {
        Queue<(string Path, int Depth)> queue = new(); queue.Enqueue((root, 0));
        for (int visited = 0; queue.Count > 0 && visited < 500; visited++)
        {
            token.ThrowIfCancellationRequested(); var current = queue.Dequeue();
            if (!Directory.Exists(current.Path)) continue;
            if (File.Exists(Path.Combine(current.Path, "bin", "java.exe"))) { yield return current.Path; continue; }
            if (current.Depth >= 3) continue;
            string[] children;
            try { children = Directory.GetDirectories(current.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.Take(200))
            {
                try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                queue.Enqueue((child, current.Depth + 1));
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryJavaHomes()
    {
        List<string> homes = [];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                foreach (var path in new[] { @"JavaSoft\Java Development Kit", @"JavaSoft\Java Runtime Environment", @"JavaSoft\JDK", @"JavaSoft\JRE", @"Eclipse Adoptium\JDK", @"Microsoft\JDK", @"Azul Systems\Zulu" })
                {
                    using var key = registry.OpenSubKey(@"SOFTWARE\" + path);
                    if (key is null) continue;
                    foreach (var version in key.GetSubKeyNames())
                    {
                        using var child = key.OpenSubKey(version);
                        if (child?.GetValue("JavaHome") is string home) homes.Add(home);
                        if (child?.GetValue("Path") is string location) homes.Add(location);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return homes;
    }

    public static IReadOnlyDictionary<string, string> ParseProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n')) { var equals = line.IndexOf('='); if (equals > 0) properties[line[..equals].Trim()] = line[(equals + 1)..].Trim(); }
        return properties;
    }
    public static string? ParseVersion(string output) => Regex.Match(output, "(?:java|openjdk) version \"([^\"]+)\"", RegexOptions.IgnoreCase) is { Success: true } match ? match.Groups[1].Value : null;
    public static int ParseMajorVersion(string version) => Regex.Match(version, @"^(?:1\.)?(\d+)(?=$|[._+\-])") is { Success: true } match && int.TryParse(match.Groups[1].Value, out var major) ? major : 0;
    private static string? Get(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : null;
    public static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch { "amd64" or "x86_64" or "x64" => "x64", "aarch64" or "arm64" => "ARM64", "x86" or "i386" or "i686" => "x86", _ => value };
    public static string NormalizeVendor(string value) => value.Trim() switch { "Oracle Corporation" => "Oracle", "Microsoft Corporation" => "Microsoft", "Amazon.com Inc." => "Amazon Corretto", "Azul Systems, Inc." => "Azul", var other => other };
}
