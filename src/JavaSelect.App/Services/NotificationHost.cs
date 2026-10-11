using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Windows.AppNotifications;

namespace JavaSelect.App.Services;

internal static class NotificationHost
{
    internal static bool ShouldRun(string[] command) => command.Contains("--notification-host") || command.Contains("--notification-probe")
        || command.Any(value => value.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal));

    internal static async Task RunAsync(string[] command)
    {
        try
        {
            if (command.Contains("--notification-probe")) await RunProbeAsync(command);
            else await RunHostAsync(command);
        }
        catch (Exception error) { Debug.WriteLine(error); Environment.ExitCode = 1; }
    }

    private static async Task RunProbeAsync(string[] command)
    {
        var index = Array.IndexOf(command, "--notification-probe");
        if (command.Length < index + 2 || !Path.IsPathFullyQualified(command[index + 1])) throw new ArgumentException("The notification probe requires an absolute report path.");
        var path = command[index + 1];
        try
        {
            await using var notifications = new NotificationService();
            var setting = await notifications.GetSettingAsync();
            var parentIdentity = NotificationProcess.DescribeIdentity();
            var senderIdentity = await notifications.GetIdentityAsync();
            uint notificationId = 0;
            DateTimeOffset? sentAt = null;
            bool? retainedByWindows = null;
            if (command.Contains("--send-test"))
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    Status = "Ready; sending in 10 seconds", ProcessId = Environment.ProcessId,
                    ParentElevated = NotificationProcess.IsElevated(), HostProcessId = notifications.HostProcessId,
                    Setting = setting.ToString(), ReadyAt = DateTimeOffset.Now,
                    ParentIdentity = parentIdentity, SenderIdentity = senderIdentity
                }, new JsonSerializerOptions { WriteIndented = true }));
                await Task.Delay(TimeSpan.FromSeconds(10));
                notificationId = (await notifications.ShowAsync("JavaSelect", "JavaSelect 通知测试：普通权限通知代理已连接。", add: "添加", ignore: "忽略")).Id;
                sentAt = DateTimeOffset.Now;
                await Task.Delay(TimeSpan.FromSeconds(15));
                retainedByWindows = notificationId != 0 && await notifications.ContainsAsync(notificationId);
            }
            if (command.Contains("--cleanup-registration")) await notifications.ClearRegistrationAsync();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId, ParentElevated = NotificationProcess.IsElevated(),
                HostProcessId = notifications.HostProcessId, Setting = setting.ToString(), NotificationId = notificationId,
                SentAt = sentAt, CheckedAt = DateTimeOffset.Now, RetainedByWindows = retainedByWindows,
                ParentIdentity = parentIdentity, SenderIdentity = senderIdentity
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunHostAsync(string[] command)
    {
        if (NotificationProcess.IsElevated()) throw new InvalidOperationException("The notification host must run without administrator privileges.");
        var index = Array.IndexOf(command, "--notification-host");
        if (index < 0) { await RelayActivationAsync(); return; }
        if (command.Length != index + 4 || !int.TryParse(command[index + 2], out var parent) || parent <= 0)
            throw new ArgumentException("Invalid notification host arguments.");
        var pipeName = command[index + 1]; var sid = command[index + 3];
        var prefix = "JavaSelect.Notification." + parent + ".";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[prefix.Length..], "N", out _)
            || NotificationProcess.CurrentSid() != sid) throw new UnauthorizedAccessException("Invalid notification host identity.");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.WaitForConnectionAsync(deadline.Token);
        NotificationProcess.VerifyClient(server, parent, sid);
        using var wire = new NotificationWire(server);
        using var notifications = new NotificationService();
        notifications.ActionInvoked += (_, action) => _ = SendActionAsync(wire, action);
        try
        {
            await notifications.InitializeAsync();
            await wire.WriteAsync(new("ready", Setting: notifications.Setting), deadline.Token);
        }
        catch (Exception error)
        {
            await wire.WriteAsync(new("error", Error: error.Message), deadline.Token);
            return;
        }
        while (server.IsConnected)
        {
            var request = await wire.ReadAsync(CancellationToken.None);
            if (request is null) return;
            if (request.Kind == "probe")
            {
                await wire.WriteAsync(new("result", request.RequestId, Setting: notifications.Setting), CancellationToken.None);
                continue;
            }
            if (request.Kind == "identity")
            {
                await wire.WriteAsync(new("result", request.RequestId, Identity: NotificationProcess.DescribeIdentity()), CancellationToken.None);
                continue;
            }
            if (request.Kind == "cleanup")
            {
                await notifications.ClearRegistrationAsync();
                await wire.WriteAsync(new("result", request.RequestId), CancellationToken.None);
                return;
            }
            if (request.Kind == "contains")
            {
                var present = await notifications.ContainsAsync(request.Id);
                await wire.WriteAsync(new("result", request.RequestId, Present: present), CancellationToken.None);
                continue;
            }
            if (request.Kind != "show" || request.Title is not { Length: > 0 and <= 1024 } || request.Body is not { Length: <= 8192 }
                || request.Add is not { Length: > 0 and <= 128 } || request.Ignore is not { Length: > 0 and <= 128 }
                || request.CandidateId is { } id && !NotificationService.IsValidAction(new("add", id)))
                throw new InvalidDataException("Invalid notification request.");
            try
            {
                var result = await notifications.ShowAsync(request.Title, request.Body, request.CandidateId, request.Add, request.Ignore);
                await wire.WriteAsync(new("result", request.RequestId, result.Id, result.Setting), CancellationToken.None);
            }
            catch (Exception error)
            {
                await wire.WriteAsync(new("error", request.RequestId, Error: error.Message), CancellationToken.None);
            }
        }
    }

    private static async Task SendActionAsync(NotificationWire wire, NotificationAction action)
    {
        try { await wire.WriteAsync(new("action", Action: action.Action, CandidateId: action.CandidateId), CancellationToken.None); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task RelayActivationAsync()
    {
        using var notifications = new NotificationService();
        var activated = new TaskCompletionSource<NotificationAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.ActionInvoked += (_, action) => activated.TrySetResult(action);
        await notifications.InitializeAsync();
        notifications.HandleLaunchActivation();
        var action = await activated.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var parameters = new List<string> { "--notification-action", action.Action };
        if (action.CandidateId is { } id) parameters.Add(id);
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Arguments = string.Join(" ", parameters.Select(NotificationProcess.QuoteArgument)),
            WorkingDirectory = AppContext.BaseDirectory
        });
    }
}

internal sealed class NotificationBroker : IDisposable, IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly NotificationWire wire;
    private readonly NotificationChild child;
    private readonly Action<NotificationAction> action;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private TaskCompletionSource<NotificationMessage>? pending;
    private int requestId;
    private bool disposed;
    internal int ProcessId => child.Id;
    internal bool IsConnected => !disposed && !lifetime.IsCancellationRequested && pipe.IsConnected;

    private NotificationBroker(NamedPipeClientStream pipe, Action<NotificationAction> action, NotificationChild child)
    {
        this.pipe = pipe; this.action = action; this.child = child; wire = new(pipe);
    }

    internal static async Task<NotificationBroker> ConnectAsync(Action<NotificationAction> action, CancellationToken token)
    {
        var name = "JavaSelect.Notification." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        var sid = NotificationProcess.CurrentSid();
        var arguments = new[] { "--notification-host", name, Environment.ProcessId.ToString(), sid };
        var host = await Task.Run(() => NotificationProcess.StartAsDesktopUser(arguments), token);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        NotificationBroker? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await pipe.ConnectAsync(timeout.Token);
            NotificationProcess.VerifyServer(pipe, host.Id, sid);
            result = new(pipe, action, host);
            var ready = await result.wire.ReadAsync(timeout.Token);
            if (ready?.Kind != "ready") throw new IOException(ready?.Error ?? "The notification host did not initialize.");
            _ = result.ReadAsync();
            return result;
        }
        catch
        {
            if (result is not null) await result.DisposeAsync();
            else { pipe.Dispose(); await host.StopAsync(); }
            throw;
        }
    }

    internal async Task<NotificationDelivery> ShowAsync(string title, string body, string? id, string add, string ignore, CancellationToken token)
    {
        var result = await RequestAsync(new("show", Title: title, Body: body, CandidateId: id, Add: add, Ignore: ignore), token);
        return new(result.Id, result.Setting);
    }

    internal async Task<AppNotificationSetting> ProbeAsync(CancellationToken token) => (await RequestAsync(new("probe"), token)).Setting;
    internal async Task ClearRegistrationAsync(CancellationToken token) => await RequestAsync(new("cleanup"), token);
    internal async Task<bool> ContainsAsync(uint id, CancellationToken token) => (await RequestAsync(new("contains", Id: id), token)).Present;
    internal async Task<NotificationIdentity> GetIdentityAsync(CancellationToken token) =>
        (await RequestAsync(new("identity"), token)).Identity ?? throw new InvalidDataException("Missing notification sender identity.");

    private async Task<NotificationMessage> RequestAsync(NotificationMessage request, CancellationToken token)
    {
        await requests.WaitAsync(token);
        try
        {
            if (!IsConnected) throw new IOException("The notification host connection was lost.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var completion = new TaskCompletionSource<NotificationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pending, completion);
            await wire.WriteAsync(request with { RequestId = ++requestId }, timeout.Token);
            var result = await completion.Task.WaitAsync(timeout.Token);
            if (result.Kind == "error") throw new IOException(result.Error ?? "The notification host could not send the notification.");
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            lifetime.Cancel(); pipe.Dispose();
            throw;
        }
        finally { Volatile.Write(ref pending, null); requests.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await wire.ReadAsync(lifetime.Token);
                if (message is null) throw new IOException("The notification host closed its connection.");
                if (message.Kind == "action" && message.Action is { } operation)
                {
                    var invoked = new NotificationAction(operation, message.CandidateId);
                    if (NotificationService.IsValidAction(invoked)) action(invoked);
                }
                else if (message.RequestId == requestId && message.Kind is "result" or "error") Volatile.Read(ref pending)?.TrySetResult(message);
            }
        }
        catch (Exception error) { Volatile.Read(ref pending)?.TrySetException(error); }
        finally { lifetime.Cancel(); }
    }

    private void Disconnect()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel(); pipe.Dispose(); wire.Dispose();
        Volatile.Read(ref pending)?.TrySetException(new ObjectDisposedException(nameof(NotificationBroker)));
    }
    public void Dispose() { Disconnect(); child.Dispose(); }
    public async ValueTask DisposeAsync() { Disconnect(); await child.StopAsync(); }
}

internal sealed class NotificationChild(SafeProcessHandle process, int id) : IDisposable
{
    internal int Id => id;
    internal async Task StopAsync()
    {
        if (process.IsClosed) return;
        try
        {
            var result = await Task.Run(() => WaitForSingleObject(process, 5000));
            if (result == 0) return;
            if (result != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!TerminateProcess(process, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (await Task.Run(() => WaitForSingleObject(process, 2000)) != 0)
                throw new IOException("The notification host did not stop before shutdown.");
        }
        finally { process.Dispose(); }
    }
    public void Dispose()
    {
        if (process.IsClosed) return;
        if (WaitForSingleObject(process, 0) == 258) TerminateProcess(process, 1);
        process.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}

internal sealed record NotificationMessage(string Kind, int RequestId = 0, uint Id = 0,
    AppNotificationSetting Setting = AppNotificationSetting.Enabled, string? Title = null, string? Body = null,
    string? CandidateId = null, string? Add = null, string? Ignore = null, string? Action = null, string? Error = null, bool Present = false,
    NotificationIdentity? Identity = null);

internal sealed record NotificationIdentity(int ProcessId, string UserName, string Sid, bool Elevated, int SessionId,
    uint ActiveConsoleSessionId, uint ExplorerProcessId, int? ExplorerSessionId, string? ExplorerUserName, string? ExplorerSid,
    string? SessionUserName, string? SessionDomainName, string LocalAppData, string UserProfile, string? Error);

internal sealed class NotificationWire(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    internal async Task WriteAsync(NotificationMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > 65536) throw new InvalidDataException("The notification message is too large.");
        await writes.WaitAsync(token);
        try
        {
            var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            await stream.WriteAsync(length, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
        }
        finally { writes.Release(); }
    }
    internal async Task<NotificationMessage?> ReadAsync(CancellationToken token)
    {
        var header = new byte[4]; var count = await stream.ReadAsync(header.AsMemory(0, 1), token);
        if (count == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > 65536) throw new InvalidDataException("Invalid notification message length.");
        var payload = new byte[size]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<NotificationMessage>(payload) ?? throw new InvalidDataException("Invalid notification message.");
    }
    public void Dispose() { }
}

internal static class NotificationProcess
{
    internal static string CurrentSid() { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    internal static bool IsElevated() { using var identity = WindowsIdentity.GetCurrent(); return IsElevated(identity.AccessToken); }
    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return elevated != 0;
    }
    internal static NotificationIdentity DescribeIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        GetWindowThreadProcessId(GetShellWindow(), out var explorerId);
        int? explorerSession = null; string? explorerUser = null, explorerSid = null, error = null;
        try
        {
            using var explorer = Process.GetProcessById(checked((int)explorerId));
            explorerSession = explorer.SessionId;
            using var process = OpenProcess(0x1000, false, checked((int)explorerId));
            if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var shellIdentity = new WindowsIdentity(token.DangerousGetHandle()))
            { explorerUser = shellIdentity.Name; explorerSid = shellIdentity.User!.Value; }
        }
        catch (Exception exception) { error = exception.Message; }
        return new(Environment.ProcessId, identity.Name, identity.User!.Value, IsElevated(identity.AccessToken), session,
            WTSGetActiveConsoleSessionId(), explorerId, explorerSession, explorerUser, explorerSid,
            SessionValue(session, 5), SessionValue(session, 7),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), error);
    }
    private static string? SessionValue(int session, int kind)
    {
        if (!WTSQuerySessionInformationW(0, session, kind, out var value, out _)) return null;
        try { return Marshal.PtrToStringUni(value); }
        finally { WTSFreeMemory(value); }
    }
    internal static void VerifyClient(NamedPipeServerStream pipe, int expectedId, string sid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedId) throw new UnauthorizedAccessException("Unexpected notification client.");
        VerifyPeer(expectedId, sid, true, Environment.ProcessPath!);
    }
    internal static void VerifyServer(NamedPipeClientStream pipe, int expectedId, string sid)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedId) throw new UnauthorizedAccessException("Unexpected notification host.");
        VerifyPeer(expectedId, sid, false, Environment.ProcessPath!);
    }
    private static void VerifyPeer(int pid, string sid, bool elevated, string path)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768); var length = buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(buffer.ToString()), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Unexpected notification process path.");
        if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User!.Value != sid || IsElevated(token) != elevated) throw new UnauthorizedAccessException("Unexpected notification process identity.");
    }
    internal static NotificationChild StartAsDesktopUser(string[] arguments)
    {
        var shell = GetShellWindow(); GetWindowThreadProcessId(shell, out var shellPid);
        if (shell == 0 || shellPid == 0) throw new InvalidOperationException("The Windows desktop is unavailable for notifications.");
        VerifyPeer((int)shellPid, CurrentSid(), false, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        using var process = OpenProcess(0x1000, false, (int)shellPid);
        if (process.IsInvalid || !OpenProcessToken(process, 0xA, out var shellToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (shellToken)
        {
            if (!DuplicateTokenEx(shellToken, 0x02000000, 0, 2, 1, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var executable = Environment.ProcessPath!;
                    var command = new StringBuilder(QuoteArgument(executable) + " " + string.Join(" ", arguments.Select(QuoteArgument)));
                    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                    if (!CreateProcessWithTokenW(token, 0, executable, command, 0x08000400, environment, AppContext.BaseDirectory, ref startup, out var child))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(child.Thread);
                    return new NotificationChild(new SafeProcessHandle(child.Process, true), checked((int)child.ProcessId));
                }
                finally { DestroyEnvironmentBlock(environment); }
            }
        }
    }
    internal static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string executable, StringBuilder arguments, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformationW(nint server, int session, int kind, out nint value, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint value);
}
