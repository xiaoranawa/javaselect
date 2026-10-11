using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JavaSelect.App.Services;

// DWM alpha technique adapted from WinUIEx (MIT), copyright 2021 Morten Nielsen.
// See THIRD-PARTY-NOTICES.md.
internal sealed class ClearBackdrop : SystemBackdrop, IDisposable
{
    private const nuint SubclassId = 0x4A534342;
    private static Windows.System.DispatcherQueueController? queue;
    private readonly SubclassProcedure procedure;
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? brush;
    private nint window;
    private bool attached;

    public ClearBackdrop(nint window)
    {
        this.window = window;
        procedure = WindowProcedure;
        try { Connect(); }
        catch { Dispose(); throw; }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        Connect();
        target.SystemBackdrop = brush;
    }

    private void Connect()
    {
        if (brush is not null) return;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, Apartment = 0 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
            try { queue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(pointer); } finally { Marshal.Release(pointer); }
        }
        compositor = new Windows.UI.Composition.Compositor();
        brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        EnableAlpha();
        if (!SetWindowSubclass(window, procedure, SubclassId, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        attached = true;
        var dc = GetDC(window);
        if (dc != 0)
        {
            try { ClearClient(dc); }
            finally { _ = ReleaseDC(window, dc); }
        }
    }

    private void EnableAlpha()
    {
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var options = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(window, ref options));
        }
        finally { _ = DeleteObject(region); }
    }

    private bool ClearClient(nint dc) => GetClientRect(window, out var bounds) && FillRect(dc, ref bounds, GetStockObject(4)) != 0;

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == 0x0014 && ClearClient((nint)wParam)) return 1; // WM_ERASEBKGND
            if (message == 0x031E) EnableAlpha(); // WM_DWMCOMPOSITIONCHANGED
            if (message == 0x0082)
            {
                _ = RemoveWindowSubclass(hwnd, procedure, SubclassId);
                attached = false; window = 0;
            }
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        Dispose();
        base.OnTargetDisconnected(target);
    }

    public void Dispose()
    {
        if (window != 0 && attached)
        {
            _ = RemoveWindowSubclass(window, procedure, SubclassId);
            var options = new BlurBehind { Flags = 1 };
            _ = DwmEnableBlurBehindWindow(window, ref options);
        }
        attached = false;
        brush?.Dispose(); compositor?.Dispose(); brush = null; compositor = null;
    }

    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int TransitionOnMaximized; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint queue);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind options);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out Rect bounds);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect bounds, nint brush);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
}
