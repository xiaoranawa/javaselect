using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using JavaSelect.App.Services;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests require Windows.");
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++; Console.WriteLine("PASS " + message);
}
T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
void UpdateProfiles(bool enabled, string directory, params string[] profiles)
{
    try { typeof(TerminalIntegration).GetMethod("UpdateProfiles", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [enabled, directory, profiles]); }
    catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
}

Check(GlobalHotkeyService.NormalizeShortcut("shift+ctrl+f23") == "Ctrl+Shift+F23", "Shortcut normalization");
Throws<FormatException>(() => GlobalHotkeyService.NormalizeShortcut("J")); passed++;
using (var first = new GlobalHotkeyService(_ => { }))
using (var blocker = new GlobalHotkeyService(_ => { }))
using (var probe = new GlobalHotkeyService(_ => { }))
{
    const string old = "Ctrl+Alt+Shift+F23", busy = "Ctrl+Alt+Shift+F24", added = "Ctrl+Alt+Shift+F22";
    first.Update([new("old", old)]); blocker.Update([new("blocker", busy)]);
    var conflict = Throws<HotkeyRegistrationException>(() => first.Update([new("new", added), new("conflict", busy)]));
    Check(conflict.NativeErrorCode == 1409 && conflict.Action == "conflict", "Native shortcut conflict identifies the rejected action");
    Check(Throws<HotkeyRegistrationException>(() => probe.Update([new("probe", old)])).NativeErrorCode == 1409, "Failed update retains the original registration");
    probe.Update([new("released", added)]); Check(true, "Failed update releases its provisional registration");
    Throws<HotkeyRegistrationException>(() => first.Update([new("a", old), new("b", old)])); passed++;
    Check(Throws<HotkeyRegistrationException>(() => blocker.Update([new("probe", old)])).NativeErrorCode == 1409, "Duplicate binding validation preserves the original shortcut");
    first.Update([]); blocker.Update([new("reclaimed", old)]); Check(true, "Removing bindings releases Windows registrations");
    blocker.Update([]); first.Update([new("again", old)]); first.Dispose();
    blocker.Update([new("after-dispose", old)]); Check(true, "Disposal releases the hotkey message thread registrations");
}
var actionReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var background = new GlobalHotkeyService(action => actionReceived.TrySetResult(action)))
{
    background.Update([new("launcher", "Ctrl+Alt+Shift+F23")]);
    var thread = (uint)typeof(GlobalHotkeyService).GetField("threadId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(background)!;
    Check(Native.PostThreadMessage(thread, 0x0312, 1, (nint)((0x86 << 16) | 7)), "Hotkey background queue accepts a native WM_HOTKEY");
    Check(await actionReceived.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "launcher", "Hotkey callback is delivered without a UI key handler");
}

var fixture = Path.Combine(Path.GetTempPath(), "JavaSelect.Platform.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    const string begin = "# JavaSelect managed integration begin", end = "# JavaSelect managed integration end";
    var config = Path.Combine(fixture, "config"); Directory.CreateDirectory(config);
    var one = Path.Combine(fixture, "pwsh", "profile.ps1"); var two = Path.Combine(fixture, "powershell", "profile.ps1");
    Directory.CreateDirectory(Path.GetDirectoryName(one)!); Directory.CreateDirectory(Path.GetDirectoryName(two)!);
    var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# existing user content\r\nfunction prompt { 'custom> ' }\r\n")).ToArray();
    File.WriteAllBytes(one, original); File.WriteAllText(two, "# user settings\n", new UTF8Encoding(false));
    var secondOriginal = File.ReadAllBytes(two);
    UpdateProfiles(true, config, one, two);
    Check(File.Exists(Path.Combine(config, "terminal-sync.enabled")), "Enabling creates the signal after writing the fixture profiles");
    Check(File.ReadAllText(one).Contains(begin) && File.ReadAllText(two).Contains(end), "Both isolated profiles receive a managed block");
    Check(File.ReadAllBytes(one).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()), "UTF-16 profile encoding is preserved");
    Check(File.ReadAllBytes(one + ".javaselect.bak").SequenceEqual(original), "Original profile backup is byte accurate");
    Check(File.ReadAllText(one).Contains("'JAVA_HOME', 'User'") && !File.ReadAllText(one).Contains("$env:PATH"), "Terminal integration synchronizes JAVA_HOME only");
    var once = File.ReadAllBytes(one);
    UpdateProfiles(true, config, one, two);
    Check(File.ReadAllBytes(one).SequenceEqual(once), "Repeated enabling does not duplicate the managed block");
    File.AppendAllText(one, "# added later\r\n", Encoding.Unicode);
    UpdateProfiles(false, config, one, two);
    Check(!File.Exists(Path.Combine(config, "terminal-sync.enabled")), "Disabling removes the signal");
    Check(File.ReadAllText(one).Contains("# added later") && !File.ReadAllText(one).Contains(begin), "Disabling preserves later user changes and removes only managed code");
    Check(File.ReadAllBytes(one + ".javaselect.bak").SequenceEqual(original), "Later changes never overwrite the initial backup");
    Check(File.ReadAllBytes(two).SequenceEqual(secondOriginal), "UTF-8 user content is restored byte accurately");
    var malformed = Path.Combine(fixture, "malformed.ps1"); File.WriteAllText(malformed, "# keep\n" + begin + "\nunfinished");
    var malformedOriginal = File.ReadAllBytes(malformed); File.WriteAllText(Path.Combine(config, "terminal-sync.enabled"), "enabled");
    Throws<AggregateException>(() => UpdateProfiles(false, config, malformed)); passed++;
    Check(!File.Exists(Path.Combine(config, "terminal-sync.enabled")), "Malformed blocks do not prevent disabling the active signal first");
    Check(File.ReadAllBytes(malformed).SequenceEqual(malformedOriginal), "Malformed profile content is never truncated");
    var rollbackConfig = Path.Combine(fixture, "rollback"); Directory.CreateDirectory(rollbackConfig);
    Directory.CreateDirectory(Path.Combine(rollbackConfig, "terminal-sync.enabled"));
    var rollbackOriginal = File.ReadAllBytes(two);
    Throws<UnauthorizedAccessException>(() => UpdateProfiles(true, rollbackConfig, two)); passed++;
    Check(File.ReadAllBytes(two).SequenceEqual(rollbackOriginal), "A failed signal commit rolls back modified profiles");
    var missing = Path.Combine(fixture, "new", "profile.ps1");
    Throws<UnauthorizedAccessException>(() => UpdateProfiles(true, rollbackConfig, missing)); passed++;
    Check(!File.Exists(missing), "Rollback removes only a newly created managed profile");
    Check(!Directory.EnumerateFiles(fixture, "*.tmp", SearchOption.AllDirectories).Any(), "Atomic profile writes leave no temporary files");
}
finally
{
    var resolved = Path.GetFullPath(fixture);
    if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(resolved).StartsWith("JavaSelect.Platform.Tests-", StringComparison.Ordinal))
        throw new IOException("Refusing to clean an unexpected test directory.");
    Directory.Delete(resolved, recursive: true);
}
Console.WriteLine($"{passed} platform checks passed. No real user environment, profile, startup entry, or notifications were modified.");

static class Native
{
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
