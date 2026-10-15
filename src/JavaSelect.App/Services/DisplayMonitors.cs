using System.Runtime.InteropServices;
using JavaSelect.Core;
using Windows.Graphics;

namespace JavaSelect.App.Services;

/// <param name="DeviceName">The Windows device name, for example \\.\DISPLAY2; the stable identifier a fixed choice is stored as.</param>
/// <param name="Bounds">The whole screen in physical pixels.</param>
/// <param name="Work">The screen without the taskbar, in physical pixels.</param>
internal sealed record DisplayMonitor(nint Handle, string DeviceName, RectInt32 Bounds, RectInt32 Work, bool IsPrimary, uint Dpi)
{
    /// <summary>The number Windows shows for this screen in its display settings (the digits of the device name).</summary>
    public int Number { get; } = int.TryParse(new string(DeviceName.Where(char.IsAsciiDigit).ToArray()), out var number) ? number : 0;
}

internal static class DisplayMonitors
{
    public static IReadOnlyList<DisplayMonitor> All()
    {
        var found = new List<DisplayMonitor>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) => { if (Describe(monitor) is { } item) found.Add(item); return true; }, 0);
        return found;
    }

    /// <summary>The screen the launcher should open on for the saved choice; null only when Windows reports no screens at all.</summary>
    public static DisplayMonitor? Resolve(string? choice)
    {
        var screens = All();
        if (screens.Count == 0) return null;
        var cursorIndex = -1;
        if (GetCursorPos(out var cursor)) cursorIndex = IndexOf(screens, MonitorFromPoint(cursor, MonitorDefaultToNearest));
        var activeWindow = GetForegroundWindow();
        var activeIndex = activeWindow == 0 ? -1 : IndexOf(screens, MonitorFromWindow(activeWindow, MonitorDefaultToNearest));
        var index = LauncherMonitorChoice.Resolve(choice, screens.Select(x => (x.DeviceName, x.IsPrimary)).ToList(), cursorIndex, activeIndex);
        return index < 0 ? null : screens[index];
    }

    private static int IndexOf(IReadOnlyList<DisplayMonitor> screens, nint handle)
    {
        for (var index = 0; index < screens.Count; index++) if (screens[index].Handle == handle) return index;
        return -1;
    }

    private static DisplayMonitor? Describe(nint monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        uint dpi = 96;
        try { if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) dpi = x; } catch (EntryPointNotFoundException) { }
        return new DisplayMonitor(monitor, info.DeviceName, ToRect(info.Monitor), ToRect(info.Work), (info.Flags & 1) != 0, dpi);
    }

    private static RectInt32 ToRect(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private const uint MonitorDefaultToNearest = 2;
    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint bounds, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
