using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace JavaSelect.App.Services;

public sealed record HotkeyBinding(string Action, string Shortcut);

public sealed class HotkeyRegistrationException(string action, string shortcut, int nativeErrorCode, string? reason = null)
    : Exception(reason ?? $"Cannot register {shortcut}: {new Win32Exception(nativeErrorCode).Message}")
{
    public string Action { get; } = action;
    public string Shortcut { get; } = shortcut;
    public int NativeErrorCode { get; } = nativeErrorCode;
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint CommandMessage = 0x8000 + 73;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<nuint, Command> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<string> events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Action<string> callback;
    private readonly Dictionary<int, Registration> active = new();
    private readonly Thread thread;
    private uint threadId;
    private nuint sequence;
    private volatile bool disposed;
    private Exception? loopError;

    public event Action<Exception>? Error;

    public GlobalHotkeyService(Action<string> callback)
    {
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        thread = new Thread(Run) { IsBackground = true, Name = "JavaSelect global hotkeys" };
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
        _ = Task.Run(DispatchAsync);
    }

    public static string NormalizeShortcut(string shortcut) => Parse(new HotkeyBinding("validate", shortcut)).Binding.Shortcut;

    public void Update(IEnumerable<HotkeyBinding> bindings)
    {
        var parsed = bindings.Select(Parse).ToArray();
        if (parsed.Length > 1024) throw new ArgumentException("Too many shortcuts.", nameof(bindings));
        var actions = new HashSet<string>(StringComparer.Ordinal);
        var gestures = new HashSet<Gesture>();
        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.Binding.Action) || !actions.Add(item.Binding.Action))
                throw new ArgumentException("Shortcut actions must be nonempty and unique.", nameof(bindings));
            if (!gestures.Add(item.Gesture))
                throw new HotkeyRegistrationException(item.Binding.Action, item.Binding.Shortcut, 1409, "Two actions use the same shortcut.");
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Send(new Command(parsed, false));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!stopped.Task.IsCompleted) Send(new Command([], true));
            disposed = true;
            stopped.Task.GetAwaiter().GetResult();
        }
    }

    private void Send(Command command)
    {
        if (stopped.Task.IsCompleted) throw new InvalidOperationException("The global shortcut thread has stopped.", loopError);
        var id = ++sequence;
        commands[id] = command;
        if (!Native.PostThreadMessage(threadId, CommandMessage, id, 0))
        {
            commands.TryRemove(id, out _);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot post to the global shortcut thread.");
        }
        Task.WhenAny(command.Completion.Task, stopped.Task).GetAwaiter().GetResult();
        if (!command.Completion.Task.IsCompleted) throw new InvalidOperationException("The global shortcut thread has stopped.", loopError);
        command.Completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0);
            threadId = Native.GetCurrentThreadId();
            ready.TrySetResult();
            while (true)
            {
                var result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Global shortcut message loop failed.");
                if (message.Message == CommandMessage && commands.TryRemove(message.WParam, out var command))
                {
                    if (command.Stop) { command.Completion.TrySetResult(); break; }
                    try { Apply(command.Registrations); command.Completion.TrySetResult(); }
                    catch (Exception error) { command.Completion.TrySetException(error); }
                }
                else if (message.Message == 0x0312 && active.TryGetValue((int)message.WParam, out var registration))
                {
                    var packed = unchecked((ulong)message.LParam);
                    if ((packed & 0xF) == registration.Gesture.Modifiers && ((packed >> 16) & 0xFFFF) == registration.Gesture.Key)
                        events.Writer.TryWrite(registration.Binding.Action);
                }
            }
        }
        catch (Exception error)
        {
            loopError = error;
            ready.TrySetException(error);
            Report(error);
        }
        finally
        {
            foreach (var id in active.Keys) Native.UnregisterHotKey(0, id);
            active.Clear();
            events.Writer.TryComplete();
            stopped.TrySetResult();
        }
    }

    private void Apply(Registration[] requested)
    {
        var previousGestures = active.ToDictionary(pair => pair.Value.Gesture, pair => pair.Key);
        var next = new Dictionary<int, Registration>();
        var added = new List<int>();
        var removed = new List<int>();
        try
        {
            foreach (var registration in requested)
            {
                if (previousGestures.TryGetValue(registration.Gesture, out var existing)) { next[existing] = registration; continue; }
                var id = 1;
                while (active.ContainsKey(id) || next.ContainsKey(id)) id++;
                if (id > 0xBFFF) throw new InvalidOperationException("No free Windows shortcut identifiers.");
                Register(id, registration);
                added.Add(id);
                next[id] = registration;
            }
            foreach (var id in active.Keys.Order())
            {
                if (next.ContainsKey(id)) continue;
                if (!Native.UnregisterHotKey(0, id)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove a previous global shortcut.");
                removed.Add(id);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var id in added)
            {
                if (!Native.UnregisterHotKey(0, id))
                {
                    active[id] = next[id];
                    failures.Add(new Win32Exception(Marshal.GetLastWin32Error(), "Cannot roll back a new shortcut."));
                }
            }
            foreach (var id in removed)
            {
                try { Register(id, active[id]); }
                catch (Exception restoreError) { active.Remove(id); failures.Add(restoreError); }
            }
            if (failures.Count > 1) throw new AggregateException("Shortcut update and rollback failed.", failures);
            throw;
        }
        active.Clear();
        foreach (var pair in next) active.Add(pair.Key, pair.Value);
    }

    private static void Register(int id, Registration registration)
    {
        if (!Native.RegisterHotKey(0, id, registration.Gesture.Modifiers | 0x4000, registration.Gesture.Key))
            throw new HotkeyRegistrationException(registration.Binding.Action, registration.Binding.Shortcut, Marshal.GetLastWin32Error());
    }

    private async Task DispatchAsync()
    {
        await foreach (var action in events.Reader.ReadAllAsync())
        {
            if (disposed) return;
            try { callback(action); }
            catch (Exception error) { Report(error); }
        }
    }

    private void Report(Exception error)
    {
        try { Error?.Invoke(error); }
        catch { }
    }

    private static Registration Parse(HotkeyBinding binding)
    {
        uint modifiers = 0;
        string? key = null;
        foreach (var token in binding.Shortcut.Split('+'))
        {
            var part = token.Trim().ToUpperInvariant();
            uint modifier = part switch { "ALT" => 1, "CTRL" or "CONTROL" => 2, "SHIFT" => 4, "WIN" or "WINDOWS" or "META" => 8, _ => 0 };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) throw new FormatException("A shortcut modifier is repeated.");
                modifiers |= modifier;
            }
            else
            {
                if (key is not null || part.Length == 0) throw new FormatException("A shortcut needs exactly one key.");
                key = part;
            }
        }
        if (modifiers == 0 || key is null) throw new FormatException("Use a modifier and a key, for example Alt+J.");
        uint code;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) code = key[0];
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24 and not 12) { code = (uint)(0x70 + number - 1); key = "F" + number; }
        else
        {
            (code, key) = key switch
            {
                "SPACE" => (0x20u, "Space"), "TAB" => (0x09u, "Tab"), "ENTER" => (0x0Du, "Enter"),
                "ESC" or "ESCAPE" => (0x1Bu, "Escape"), "BACKSPACE" => (0x08u, "Backspace"),
                "DELETE" => (0x2Eu, "Delete"), "INSERT" => (0x2Du, "Insert"), "HOME" => (0x24u, "Home"), "END" => (0x23u, "End"),
                "PAGEUP" or "PGUP" => (0x21u, "PageUp"), "PAGEDOWN" or "PGDN" => (0x22u, "PageDown"),
                "UP" => (0x26u, "Up"), "DOWN" => (0x28u, "Down"), "LEFT" => (0x25u, "Left"), "RIGHT" => (0x27u, "Right"),
                "PLUS" => (0xBBu, "Plus"), "MINUS" => (0xBDu, "Minus"), "COMMA" => (0xBCu, "Comma"), "PERIOD" => (0xBEu, "Period"),
                _ => throw new FormatException("Unsupported shortcut key. F12 is reserved by Windows.")
            };
        }
        var names = new List<string>();
        foreach (var (mask, name) in new[] { (2u, "Ctrl"), (1u, "Alt"), (4u, "Shift"), (8u, "Win") })
            if ((modifiers & mask) != 0) names.Add(name);
        names.Add(key);
        return new Registration(binding with { Shortcut = string.Join('+', names) }, new Gesture(modifiers, code));
    }

    private readonly record struct Gesture(uint Modifiers, uint Key);
    private sealed record Registration(HotkeyBinding Binding, Gesture Gesture);
    private sealed record Command(Registration[] Registrations, bool Stop)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMessage { public nint HWnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
        [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out NativeMessage message, nint hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] internal static extern int GetMessage(out NativeMessage message, nint hwnd, uint min, uint max);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    }
}
