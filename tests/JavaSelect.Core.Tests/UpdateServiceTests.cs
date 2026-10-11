using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JavaSelect.App.Services;

internal static class UpdateServiceTests
{
    public static async Task RunAsync(string temporaryRoot, Func<string, Func<Task>, Task> check, Func<string, string, Task> createJunction)
    {
        var fixture = Path.Combine(temporaryRoot, "updates-tests"); Directory.CreateDirectory(fixture);
        var installed = Path.Combine(fixture, "installed"); Directory.CreateDirectory(installed);
        var executable = Path.Combine(installed, "JavaSelect.exe"); await File.WriteAllTextAsync(executable, "fixture only; never executed");
        var bytes = Encoding.UTF8.GetBytes("isolated installer fixture");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        const string tag = "v65534.1.1", version = "65534.1.1";
        const string name = "JavaSelect-65534.1.1-win-x64-setup.exe";
        const string address = "https://github.com/xiaoranawa/javaselect/releases/download/" + tag + "/";
        var release = new ReleaseInfo(tag, "https://github.com/xiaoranawa/javaselect/releases/tag/" + tag, true, new(name, address + name, bytes.Length, hash));
        var sequence = 0;
        string Configuration() => Path.Combine(fixture, "config-" + ++sequence);
        UpdateService Service(string directory, HttpMessageHandler handler, TimeSpan? timeout = null) => new(directory, handler, () => installed, () => executable, timeout);
        static void Assert(bool condition) { if (!condition) throw new Exception("Update assertion failed."); }
        static async Task Reject<T>(Func<Task> action) where T : Exception
        {
            try { await action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        void CleanFailure(string directory)
        {
            var updates = Path.Combine(directory, "updates");
            Assert(!Directory.Exists(updates) || !Directory.EnumerateFiles(updates).Any(x => x.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        }

        await check("Update metadata requires official stable assets and checksum proof", () =>
        {
            string Json(string url = address + name, string digest = "", string releaseTag = tag) => JsonSerializer.Serialize(new
            {
                tag_name = releaseTag, html_url = "https://github.com/xiaoranawa/javaselect/releases/tag/" + releaseTag,
                draft = false, prerelease = false,
                assets = new[] { new { name, browser_download_url = url, size = bytes.Length, digest = digest.Length == 0 ? null : digest, state = "uploaded" } }
            });
            var accepted = CommunityService.ParseRelease(Json(digest: "sha256:" + hash), "0.1.0");
            Assert(accepted.IsNew && accepted.Installer?.Sha256 == hash);
            Assert(CommunityService.ParseRelease(Json(), "0.1.0").Installer is null);
            foreach (var invalid in new[] { "https://github.com/attacker/javaselect/releases/download/" + tag + "/" + name, address + name + "?download=1", "https://github.com.evil.test/" + name })
            {
                try { CommunityService.ParseRelease(Json(invalid, "sha256:" + hash), "0.1.0"); throw new Exception("Accepted invalid asset."); } catch (IOException) { }
            }
            try { CommunityService.ParseRelease(Json(releaseTag: "v1.2.3-beta"), "0.1.0"); throw new Exception("Accepted prerelease tag."); } catch (IOException) { }
            return Task.CompletedTask;
        });
        await check("Installed update detection rejects portable, sibling, and nested executable paths", () =>
        {
            using var service = Service(Configuration(), new Handler(_ => Response(bytes)));
            Assert(service.GetInstallation()?.InstallDirectory == installed);
            foreach (var path in new[] { Path.Combine(installed + "-portable", "JavaSelect.exe"), Path.Combine(installed, "other", "JavaSelect.exe"), Path.Combine(installed, "Other.exe") })
            {
                using var portable = new UpdateService(Configuration(), new Handler(_ => Response(bytes)), () => installed, () => path);
                Assert(portable.GetInstallation() is null);
            }
            using var unregistered = new UpdateService(Configuration(), new Handler(_ => Response(bytes)), () => null, () => executable);
            Assert(unregistered.GetInstallation() is null);
            return Task.CompletedTask;
        });
        await check("Installer download verifies API digest, atomically saves, reuses verified file, and detects tampering", async () =>
        {
            var directory = Configuration(); var handler = new Handler(_ => Response(bytes));
            using var service = Service(directory, handler); var progress = new RecordingProgress();
            var result = await service.DownloadInstallerAsync(release, progress);
            Assert(result.Version == version && result.Sha256 == hash && result.Path == Path.Combine(directory, "updates", name));
            Assert((await File.ReadAllBytesAsync(result.Path)).SequenceEqual(bytes) && await service.VerifyInstallerAsync(result));
            Assert(progress.Values[0] == 0 && progress.Values[^1] == 100 && progress.Values.SequenceEqual(progress.Values.Order()) && progress.Values.Count <= 101);
            await service.DownloadInstallerAsync(release); Assert(handler.Requests.Count == 1);
            await File.WriteAllTextAsync(result.Path, "tampered"); Assert(!await service.VerifyInstallerAsync(result));
            var outside = Path.Combine(fixture, "outside.exe"); await File.WriteAllBytesAsync(outside, bytes);
            Assert(!await service.VerifyInstallerAsync(result with { Path = outside }));
        });
        await check("Official SHA256SUMS fallback verifies the exact installer entry", async () =>
        {
            var manifest = Encoding.UTF8.GetBytes(hash + "  " + name + "\n"); var directory = Configuration();
            using var service = Service(directory, new Handler(request => Response(request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") ? manifest : bytes)));
            var result = await service.DownloadInstallerAsync(release with { Installer = release.Installer! with { Sha256 = null }, Checksums = new("SHA256SUMS.txt", address + "SHA256SUMS.txt", manifest.Length) });
            Assert(result.Sha256 == hash && await service.VerifyInstallerAsync(result));
        });
        if (OperatingSystem.IsWindows()) await check("A verified installer handle blocks overwrite and replacement until disposed", async () =>
        {
            var directory = Configuration(); using var service = Service(directory, new Handler(_ => Response(bytes)));
            var result = await service.DownloadInstallerAsync(release);
            var replacement = Path.Combine(directory, "replacement.tmp"); await File.WriteAllTextAsync(replacement, "replacement");
            await using (var locked = await service.OpenVerifiedInstallerAsync(result))
            {
                Assert(locked is not null && locked.Position == 0);
                await Reject<IOException>(() => File.WriteAllTextAsync(result.Path, "overwrite"));
                var replacementBlocked = false;
                try { File.Move(replacement, result.Path, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { replacementBlocked = true; }
                Assert(replacementBlocked);
                Assert((await File.ReadAllBytesAsync(result.Path)).SequenceEqual(bytes));
            }
            File.Move(replacement, result.Path, true);
            Assert(await File.ReadAllTextAsync(result.Path) == "replacement" && !await service.VerifyInstallerAsync(result));
        });
        await check("Malformed or duplicated manifest entries cannot authorize an installer", async () =>
        {
            foreach (var content in new[] { hash + "  other.exe\n", hash + "  " + name + "\n" + hash + "  " + name + "\n", "bad manifest" })
            {
                var manifest = Encoding.UTF8.GetBytes(content); var directory = Configuration(); var handler = new Handler(_ => Response(manifest));
                using var service = Service(directory, handler);
                await Reject<IOException>(() => service.DownloadInstallerAsync(release with { Installer = release.Installer! with { Sha256 = null }, Checksums = new("SHA256SUMS.txt", address + "SHA256SUMS.txt", manifest.Length) }));
                Assert(handler.Requests.Count == 1); CleanFailure(directory);
            }
        });
        await check("Invalid installer sources, sizes, and missing checksum fail before downloading", async () =>
        {
            foreach (var asset in new[]
            {
                release.Installer! with { Url = "https://evil.test/" + name }, release.Installer! with { Name = "other.exe" },
                release.Installer! with { Size = UpdateService.MaxInstallerBytes + 1 }, release.Installer! with { Size = 0 },
                release.Installer! with { Sha256 = null }, release.Installer! with { Sha256 = "not-a-hash" }
            })
            {
                var directory = Configuration(); var handler = new Handler(_ => Response(bytes)); using var service = Service(directory, handler);
                await Reject<IOException>(() => service.DownloadInstallerAsync(release with { Installer = asset }));
                Assert(handler.Requests.Count == 0); CleanFailure(directory);
            }
        });
        await check("External redirects are rejected; GitHub release-storage redirects are accepted", async () =>
        {
            var directory = Configuration(); var rejectedHandler = new Handler(_ => Redirect("https://evil.test/installer.exe"));
            using (var service = Service(directory, rejectedHandler)) await Reject<IOException>(() => service.DownloadInstallerAsync(release));
            Assert(rejectedHandler.Requests.Count == 1); CleanFailure(directory);
            var handler = new Handler(request => request.RequestUri!.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/asset?signature=test") : Response(bytes));
            using var accepted = Service(Configuration(), handler);
            Assert(await accepted.VerifyInstallerAsync(await accepted.DownloadInstallerAsync(release))); Assert(handler.Requests.Count == 2);
        });
        await check("Truncated, oversized chunked, and hash-mismatched downloads leave no installer or temporary file", async () =>
        {
            foreach (var body in new[] { bytes[..^1], bytes.Concat(new byte[] { 1 }).ToArray(), bytes.Reverse().ToArray() })
            {
                var directory = Configuration(); using var service = Service(directory, new Handler(_ => Response(body, chunked: true)));
                await Reject<IOException>(() => service.DownloadInstallerAsync(release)); CleanFailure(directory);
            }
        });
        await check("Cancellation removes only its temporary download and preserves existing files", async () =>
        {
            var directory = Configuration(); var updates = Path.Combine(directory, "updates"); Directory.CreateDirectory(updates);
            var existing = Path.Combine(updates, name); await File.WriteAllTextAsync(existing, "previous installer");
            var unrelated = Path.Combine(updates, "other.tmp"); await File.WriteAllTextAsync(unrelated, "keep");
            using var cancellation = new CancellationTokenSource();
            using var service = Service(directory, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(bytes, cancellation.Cancel)) }));
            await Reject<OperationCanceledException>(() => service.DownloadInstallerAsync(release, cancellationToken: cancellation.Token));
            Assert(await File.ReadAllTextAsync(existing) == "previous installer" && await File.ReadAllTextAsync(unrelated) == "keep");
            Assert(Directory.GetFiles(updates).Length == 2);
        });
        await check("Installer request timeout is bounded and cannot leave a runnable file", async () =>
        {
            var directory = Configuration(); using var service = Service(directory, new DelayedHandler(), TimeSpan.FromMilliseconds(30));
            await Reject<TimeoutException>(() => service.DownloadInstallerAsync(release)); CleanFailure(directory);
        });
        if (OperatingSystem.IsWindows()) await check("Update staging refuses junctions without touching their target", async () =>
        {
            var directory = Configuration(); Directory.CreateDirectory(directory);
            var outside = Path.Combine(fixture, "junction-target"); Directory.CreateDirectory(outside);
            var link = Path.Combine(directory, "updates"); await createJunction(link, outside);
            try
            {
                var handler = new Handler(_ => Response(bytes)); using var service = Service(directory, handler);
                await Reject<IOException>(() => service.DownloadInstallerAsync(release));
                Assert(!Directory.EnumerateFileSystemEntries(outside).Any() && handler.Requests.Count == 0);
            }
            finally { Directory.Delete(link); }
        });
    }

    private static HttpResponseMessage Response(byte[] bytes, bool chunked = false) => new(HttpStatusCode.OK)
        { Content = chunked ? new StreamContent(new ChunkedStream(bytes)) : new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string destination)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(destination); return response;
    }
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!); return Task.FromResult(respond(request));
        }
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("Unreachable"); }
    }
    private sealed class ChunkedStream(byte[] bytes, Action? firstRead = null) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var count = Math.Min(4, bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer); offset += count;
            if (offset == count && count > 0) firstRead?.Invoke();
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
