using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JavaSelect.App.Services;

public sealed record InstalledApplication(string InstallDirectory, string ExecutablePath);
public sealed record DownloadedInstaller(string Version, string Path, string Sha256, long Size);

public sealed class UpdateService : IDisposable
{
    public const long MaxInstallerBytes = 512L * 1024 * 1024;
    public const long MaxManifestBytes = 64 * 1024;
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6402C049-C5E4-4617-BD78-83741CB00650}_is1";
    private readonly string updatesDirectory;
    private readonly HttpClient client;
    private readonly Func<string?> installLocationProvider, processPathProvider;
    private readonly TimeSpan downloadTimeout;
    private readonly SemaphoreSlim downloads = new(1);

    public UpdateService(string configurationDirectory, HttpMessageHandler? handler = null,
        Func<string?>? installLocationProvider = null, Func<string?>? processPathProvider = null, TimeSpan? downloadTimeout = null)
    {
        updatesDirectory = Path.Combine(Path.GetFullPath(configurationDirectory), "updates");
        this.installLocationProvider = installLocationProvider ?? ReadInstallLocation;
        this.processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
        if (this.downloadTimeout <= TimeSpan.Zero || this.downloadTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        client = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"JavaSelect/{BuildInfo.Version} (+https://github.com/xiaoranawa/javaselect)");
    }

    public InstalledApplication? GetInstallation()
    {
        try
        {
            var location = installLocationProvider(); var executable = processPathProvider();
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(location) || !Path.IsPathFullyQualified(executable)) return null;
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var expected = Path.Combine(directory, "JavaSelect.exe");
            if (!string.Equals(expected, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) || !File.Exists(expected)) return null;
            EnsureNoReparsePoints(expected);
            return new(directory, expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) { return null; }
    }

    public async Task<DownloadedInstaller> DownloadInstallerAsync(ReleaseInfo release, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (GetInstallation() is null) throw new InvalidOperationException("Automatic upgrades are only available for the installed copy of JavaSelect.");
        var version = ReleaseValidation.NormalizeVersion(release.Version);
        if (!release.IsNew || Version.Parse(version) <= Version.Parse(ReleaseValidation.NormalizeVersion(BuildInfo.Version))) throw new IOException("The release is not newer than this application.");
        if (!ReleaseValidation.IsOfficialAddress(release.Url, "/releases/tag/" + release.Version)) throw new IOException("Invalid release address.");
        var asset = release.Installer ?? throw new IOException("The release does not include a verified Windows installer.");
        ReleaseValidation.ValidateAsset(asset, release.Version, $"JavaSelect-{version}-win-x64-setup.exe", MaxInstallerBytes);
        await downloads.WaitAsync(cancellationToken);
        string? temporary = null; var temporaryCreated = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(downloadTimeout);
            var token = deadline.Token;
            progress?.Report(0);
            var expectedHash = asset.Sha256 is not null ? ReleaseValidation.NormalizeHash(asset.Sha256) : await ReadExpectedHashAsync(release, asset.Name, token);
            var target = Path.Combine(updatesDirectory, asset.Name);
            var result = new DownloadedInstaller(version, target, expectedHash, asset.Size);
            if (await VerifyInstallerAsync(result, token)) { progress?.Report(100); return result; }
            EnsureNoReparsePoints(updatesDirectory);
            Directory.CreateDirectory(updatesDirectory);
            EnsureNoReparsePoints(updatesDirectory);
            using var response = await SendAsync(new Uri(asset.Url), token);
            CheckContentLength(response, asset.Size);
            temporary = Path.Combine(updatesDirectory, $".{asset.Name}.{Guid.NewGuid():N}.tmp");
            var outputFile = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            temporaryCreated = true;
            await using (var output = outputFile)
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long received = 0; int count; var lastProgress = 0;
                while ((count = await ReadChunkAsync(input, buffer, token)) != 0)
                {
                    received += count;
                    if (received > asset.Size) throw new IOException("The downloaded installer exceeds its published size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    var percentage = (int)Math.Min(99, received * 100d / asset.Size);
                    if (percentage != lastProgress) { lastProgress = percentage; progress?.Report(percentage); }
                }
                if (received != asset.Size) throw new IOException("The installer download is incomplete.");
                if (!MatchesHash(hash.GetHashAndReset(), expectedHash)) throw new IOException("The installer SHA-256 checksum does not match the release.");
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(target);
            File.Move(temporary, target, overwrite: true);
            temporary = null;
            progress?.Report(100);
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The update download timed out. Please try again.", error); }
        finally
        {
            if (temporaryCreated && temporary is not null)
            {
                try { EnsureNoReparsePoints(temporary); File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            downloads.Release();
        }
    }

    public async Task<bool> VerifyInstallerAsync(DownloadedInstaller installer, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenVerifiedInstallerAsync(installer, cancellationToken);
        return stream is not null;
    }

    public async Task<FileStream?> OpenVerifiedInstallerAsync(DownloadedInstaller installer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? stream = null;
        try
        {
            var version = ReleaseValidation.NormalizeVersion(installer.Version);
            var expected = Path.Combine(updatesDirectory, $"JavaSelect-{version}-win-x64-setup.exe");
            if (installer.Size <= 0 || installer.Size > MaxInstallerBytes || !string.Equals(Path.GetFullPath(installer.Path), expected, StringComparison.OrdinalIgnoreCase)) return null;
            var hash = ReleaseValidation.NormalizeHash(installer.Sha256);
            EnsureNoReparsePoints(expected);
            stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != installer.Size || !MatchesHash(await SHA256.HashDataAsync(stream, cancellationToken), hash)) return null;
            stream.Position = 0;
            var verified = stream; stream = null;
            return verified;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) { return null; }
        finally { if (stream is not null) await stream.DisposeAsync(); }
    }

    private async Task<string> ReadExpectedHashAsync(ReleaseInfo release, string installerName, CancellationToken token)
    {
        var manifest = release.Checksums ?? throw new IOException("The release does not provide an installer checksum.");
        ReleaseValidation.ValidateAsset(manifest, release.Version, "SHA256SUMS.txt", MaxManifestBytes);
        using var response = await SendAsync(new Uri(manifest.Url), token);
        CheckContentLength(response, manifest.Size);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await ReadChunkAsync(input, buffer, token)) != 0)
        {
            if (output.Length + count > manifest.Size) throw new IOException("The checksum file exceeds its published size.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != manifest.Size) throw new IOException("The checksum file download is incomplete.");
        var bytes = output.ToArray();
        if (manifest.Sha256 is not null && !MatchesHash(SHA256.HashData(bytes), ReleaseValidation.NormalizeHash(manifest.Sha256))) throw new IOException("The checksum file does not match the release.");
        string? result = null;
        foreach (var line in new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Regex.Match(line.TrimEnd('\r'), @"^([0-9a-fA-F]{64}) [ *]([^\r\n]+)$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new IOException("The release checksum file is invalid.");
            if (match.Groups[2].Value != installerName) continue;
            if (result is not null) throw new IOException("The checksum file contains duplicate installer entries.");
            result = match.Groups[1].Value.ToLowerInvariant();
        }
        return result ?? throw new IOException("The checksum file does not list this installer.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri source, CancellationToken token)
    {
        var uri = source;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new IOException("The download redirect has no destination.");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!AllowedRedirect(next, source)) throw new IOException("The download redirected outside GitHub's release storage.");
                uri = next;
                continue;
            }
            try
            {
                if (response.RequestMessage?.RequestUri is { } finalUri && !AllowedRedirect(finalUri, source)) throw new IOException("Invalid download destination.");
                if (response.StatusCode != HttpStatusCode.OK) { response.EnsureSuccessStatusCode(); throw new IOException("The release server returned an unexpected response."); }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new IOException("The release download redirected too many times.");
    }

    private static bool AllowedRedirect(Uri uri, Uri source) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri == source || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static async Task<int> ReadChunkAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await input.ReadAsync(buffer, deadline.Token);
    }
    private static void CheckContentLength(HttpResponseMessage response, long expected)
    {
        if (response.Content.Headers.ContentLength is { } length && length != expected) throw new IOException("The download size does not match the release.");
    }
    private static bool MatchesHash(byte[] actual, string expected) => CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
    private static string? ReadInstallLocation()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var installation = currentUser.OpenSubKey(UninstallKey, writable: false);
        return installation?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("The update path contains a symbolic link or directory junction."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public void Dispose() { client.Dispose(); downloads.Dispose(); }
}

internal static class ReleaseValidation
{
    internal static string NormalizeVersion(string value)
    {
        var normalized = value.StartsWith('v') || value.StartsWith('V') ? value[1..] : value;
        if (!Regex.IsMatch(normalized, @"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$", RegexOptions.CultureInvariant)
            || normalized.Split('.').Any(x => int.Parse(x) > 65535)) throw new IOException("The release version is not a supported stable version.");
        return normalized;
    }
    internal static string NormalizeHash(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new IOException("The release SHA-256 checksum is invalid.");
        return value.ToLowerInvariant();
    }
    internal static bool IsOfficialAddress(string address, string suffix) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == "/" + JavaSelect.Core.ProjectInfo.Repository + suffix;
    internal static void ValidateAsset(ReleaseAsset asset, string tag, string expectedName, long maxSize)
    {
        _ = NormalizeVersion(tag);
        if (asset.Name != expectedName || !IsOfficialAddress(asset.Url, $"/releases/download/{tag}/{expectedName}") || asset.Size <= 0 || asset.Size > maxSize)
            throw new IOException("The release asset address, filename, or size is invalid.");
        if (asset.Sha256 is not null) _ = NormalizeHash(asset.Sha256);
    }
}
