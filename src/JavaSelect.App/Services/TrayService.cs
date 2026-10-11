using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JavaSelect.App.Services;

public sealed class TrayService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 82;
    private const uint RefreshMessage = 0x8000 + 83;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action open;
    private readonly Action exit;
    private readonly string iconPath;
    private readonly Native.WindowProcedure procedure;
    private readonly string className = "JavaSelect.Tray." + Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private volatile string tooltip;
    private volatile string openText;
    private volatile string exitText;
    private nint hwnd;
    private nint icon;
    private bool ownsIcon;
    private bool iconAdded;
    private uint taskbarCreated;
    private bool disposed;
    public event Action<Exception>? Error;

    public TrayService(string iconPath, string tooltip, Action open, Action exit, string openText = "Open", string exitText = "Exit")
    {
        this.iconPath = iconPath;
        this.tooltip = tooltip;
        this.openText = openText;
        this.exitText = exitText;
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.exit = exit ?? throw new ArgumentNullException(nameof(exit));
        procedure = WindowProcedure;
        new Thread(Run) { IsBackground = true, Name = "JavaSelect notification area" }.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public void UpdateText(string tooltip, string openText, string exitText)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            this.tooltip = tooltip;
            this.openText = openText;
            this.exitText = exitText;
            if (!Native.PostMessage(hwnd, RefreshMessage, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!stopped.Task.IsCompleted && !Native.PostMessage(hwnd, 0x0010, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            stopped.Task.GetAwaiter().GetResult();
            disposed = true;
        }
    }

    private void Run()
    {
        var instance = Native.GetModuleHandle(null);
        ushort atom = 0;
        try
        {
            var windowClass = new Native.WindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, ClassName = className };
            atom = Native.RegisterClass(ref windowClass);
            if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            hwnd = Native.CreateWindowEx(0, className, "JavaSelect", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            icon = Native.LoadImage(0, iconPath, 1, 0, 0, 0x10 | 0x40);
            ownsIcon = icon != 0;
            if (icon == 0) icon = Native.LoadIcon(0, (nint)32512);
            AddIcon();
            ready.TrySetResult();
            while (true)
            {
                var result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch (Exception error) { ready.TrySetException(error); Report(error); }
        finally
        {
            RemoveIcon();
            if (hwnd != 0) Native.DestroyWindow(hwnd);
            if (ownsIcon) Native.DestroyIcon(icon);
            if (atom != 0) Native.UnregisterClass(className, instance);
            stopped.TrySetResult();
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (taskbarCreated != 0 && message == taskbarCreated) { iconAdded = false; AddIcon(); return 0; }
            if (message == RefreshMessage) { var data = IconData(); Native.ShellNotifyIcon(1, ref data); return 0; }
            if (message == CallbackMessage)
            {
                var notification = (uint)((ulong)lParam & 0xFFFF);
                if (notification is 0x0400 or 0x0401 or 0x0202 or 0x0203) Invoke(open);
                else if (notification is 0x007B or 0x0205) ShowMenu();
                return 0;
            }
            if (message == 0x0010) { RemoveIcon(); Native.DestroyWindow(window); return 0; }
            if (message == 0x0002) { Native.PostQuitMessage(0); return 0; }
        }
        catch (Exception error) { Report(error); }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Native.AppendMenu(menu, 0, 1, openText);
            Native.AppendMenu(menu, 0x800, 0, null);
            Native.AppendMenu(menu, 0, 2, exitText);
            Native.GetCursorPos(out var position);
            Native.SetForegroundWindow(hwnd);
            var command = Native.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, position.X, position.Y, hwnd, 0);
            Native.PostMessage(hwnd, 0, 0, 0);
            if (command == 1) Invoke(open);
            else if (command == 2) Invoke(exit);
        }
        finally { Native.DestroyMenu(menu); }
    }

    private void AddIcon()
    {
        var data = IconData();
        if (!Native.ShellNotifyIcon(0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the JavaSelect notification-area icon.");
        iconAdded = true;
        data.Version = 4;
        Native.ShellNotifyIcon(4, ref data);
    }

    private void RemoveIcon()
    {
        if (!iconAdded) return;
        var data = IconData();
        Native.ShellNotifyIcon(2, ref data);
        iconAdded = false;
    }

    private Native.NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<Native.NotifyIconData>(), Window = hwnd, Id = 1, Flags = 1 | 2 | 4 | 0x80,
        Callback = CallbackMessage, Icon = icon, Tip = tooltip.Length < 128 ? tooltip : tooltip[..127], Info = "", InfoTitle = ""
    };

    private void Invoke(Action action) => ThreadPool.QueueUserWorkItem(_ => { try { action(); } catch (Exception error) { Report(error); } });
    private void Report(Exception error) { try { Error?.Invoke(error); } catch { } }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
        { public uint Style; public nint Procedure; public int ClassExtra; public int WindowExtra; public nint Instance; public nint Icon; public nint Cursor; public nint Background; [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct NotifyIconData
        { public uint Size; public nint Window; public uint Id; public uint Flags; public uint Callback; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State; public uint StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)] internal static extern ushort RegisterClass(ref WindowClass windowClass);
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint load);
        [DllImport("user32.dll", EntryPoint = "LoadIconW")] internal static extern nint LoadIcon(nint instance, nint name);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
        [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
        [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
        [DllImport("user32.dll")] internal static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    }
}
