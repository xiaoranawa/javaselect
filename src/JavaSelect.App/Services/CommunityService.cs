using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JavaSelect.App.Services;

public sealed record Contributor(string Login, string AvatarUrl, string ProfileUrl, int Contributions);
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256 = null);
public sealed record ReleaseInfo(string Version, string Url, bool IsNew, ReleaseAsset? Installer = null,
    ReleaseAsset? Checksums = null, ReleaseAsset? Portable = null);
public sealed record QuoteInfo(string Text, string Source);

public sealed class CommunityService : IDisposable
{
    private readonly string cache;
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> requests = new(StringComparer.Ordinal);
    public CommunityService(string directory, HttpMessageHandler? handler = null)
    {
        cache = directory;
        client = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"JavaSelect/{BuildInfo.Version} (+https://github.com/xiaoranawa/javaselect)");
    }
    public async Task<IReadOnlyList<Contributor>> GetContributorsAsync(string repository, CancellationToken token)
    {
        var path = Repository(repository); var people = new List<Contributor>();
        for (var page = 1; page <= 5; page++)
        {
            var data = await ReadAsync($"https://api.github.com/repos/{path}/contributors?per_page=100&page={page}", token);
            using var json = JsonDocument.Parse(data!);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("Invalid contributors response.");
            foreach (var user in json.RootElement.EnumerateArray())
            {
                var login = user.GetProperty("login").GetString() ?? "";
                if (!Regex.IsMatch(login, "^[A-Za-z0-9_-]{1,64}$")) continue;
                var avatar = user.GetProperty("avatar_url").GetString() ?? "";
                if (!Uri.TryCreate(avatar, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "avatars.githubusercontent.com") avatar = "";
                people.Add(new Contributor(login, avatar, "https://github.com/" + login, user.GetProperty("contributions").GetInt32()));
            }
            if (json.RootElement.GetArrayLength() < 100) break;
        }
        return people.DistinctBy(x => x.Login).ToArray();
    }
    public async Task<ReleaseInfo?> GetReleaseAsync(string repository, string current, CancellationToken token)
    {
        if (!string.Equals(repository, JavaSelect.Core.ProjectInfo.Repository, StringComparison.OrdinalIgnoreCase)) throw new IOException("Updates must come from the official JavaSelect repository.");
        var path = JavaSelect.Core.ProjectInfo.Repository;
        var data = await ReadAsync($"https://api.github.com/repos/{path}/releases/latest", token, allowMissing: true);
        if (data is null) { _ = await ReadAsync($"https://api.github.com/repos/{path}", token); return null; }
        return ParseRelease(data, current);
    }
    internal static ReleaseInfo ParseRelease(string data, string current)
    {
        using var json = JsonDocument.Parse(data); var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ReleaseValidation.NormalizeVersion(tag);
        var address = root.GetProperty("html_url").GetString() ?? "";
        if (!ReleaseValidation.IsOfficialAddress(address, "/releases/tag/" + tag)) throw new IOException("Invalid release address.");
        if ((root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) || (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())) throw new IOException("Only stable releases can be installed.");
        var available = ParseVersion(tag); var installed = ParseVersion(current);
        var installer = Asset($"JavaSelect-{version}-win-x64-setup.exe", UpdateService.MaxInstallerBytes);
        var checksums = Asset("SHA256SUMS.txt", UpdateService.MaxManifestBytes);
        var portable = Asset($"JavaSelect-{version}-win-x64-portable.zip", UpdateService.MaxInstallerBytes);
        return new(tag, address, available > installed, installer?.Sha256 is not null || checksums is not null ? installer : null, checksums, portable);

        ReleaseAsset? Asset(string expectedName, long limit)
        {
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            ReleaseAsset? result = null;
            foreach (var item in assets.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name) || name.GetString() != expectedName) continue;
                if (result is not null) throw new IOException("The release contains duplicate assets.");
                if (item.TryGetProperty("state", out var state) && state.GetString() != "uploaded") throw new IOException("The release asset is not ready.");
                var digest = item.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                string? sha256 = null;
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal)) sha256 = ReleaseValidation.NormalizeHash(digest[7..]);
                result = new(expectedName, item.GetProperty("browser_download_url").GetString() ?? "", item.GetProperty("size").GetInt64(), sha256);
                ReleaseValidation.ValidateAsset(result, tag, expectedName, limit);
            }
            return result;
        }
    }
    public async Task<QuoteInfo> GetQuoteAsync(CancellationToken token)
    {
        using var json = JsonDocument.Parse((await ReadAsync("https://v1.hitokoto.cn/?encode=json", token))!);
        var root = json.RootElement; var quote = root.GetProperty("hitokoto").GetString() ?? "";
        if (quote.Length == 0 || quote.Length > 1000) throw new IOException("Invalid quote response.");
        var source = root.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "";
        return new(quote, source);
    }
    private static string Repository(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$") || value.EndsWith("/.") || value.EndsWith("/..")) throw new FormatException("Use owner/repository.");
        return value;
    }
    private static Version ParseVersion(string value)
    {
        var cleaned = value.TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(cleaned, out var version)) throw new FormatException("Release version is not a supported version number.");
        return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
    private sealed record Cached(string Body, string? ETag);
    private async Task<string?> ReadAsync(string url, CancellationToken token, bool allowMissing = false)
    {
        var gate = requests.GetOrAdd(url, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(cache);
            var path = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
            Cached? saved = null;
            try { if (File.Exists(path) && new FileInfo(path).Length < 3 * 1024 * 1024) saved = JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(path, token)); } catch (JsonException) { }
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var requestToken = deadline.Token;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (saved?.ETag is { } tag) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
                if (response.StatusCode == HttpStatusCode.NotModified && saved is not null) return saved.Body;
                if (response.StatusCode == HttpStatusCode.NotFound && allowMissing) return null;
                if (response.StatusCode == HttpStatusCode.NoContent) return "[]";
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new IOException("Response is too large.");
                await using var input = await response.Content.ReadAsStreamAsync(requestToken); using var buffer = new MemoryStream();
                var bytes = new byte[8192]; int count;
                while ((count = await input.ReadAsync(bytes, requestToken)) > 0) { if (buffer.Length + count > 2 * 1024 * 1024) throw new IOException("Response is too large."); buffer.Write(bytes, 0, count); }
                var body = Encoding.UTF8.GetString(buffer.ToArray()); using var validation = JsonDocument.Parse(body);
                var temporary = path + ".tmp"; await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Cached(body, response.Headers.ETag?.ToString())), token); File.Move(temporary, path, true);
                return body;
            }
            catch (Exception error) when (!token.IsCancellationRequested && saved is not null && error is HttpRequestException or TaskCanceledException) { return saved.Body; }
        }
        finally { gate.Release(); }
    }
    public void Dispose() => client.Dispose();
}
