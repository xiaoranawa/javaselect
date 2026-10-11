using System.Numerics;
using System.Runtime.InteropServices;
using JavaSelect.App.Services;
using JavaSelect.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace JavaSelect.App;

public sealed partial class LauncherWindow : Window
{
    private readonly AppController controller;
    private readonly nint hwnd;
    private bool showing, exiting;
    private Task<bool>? pendingSwitch;
    private string? pendingTarget;
    private int presentationVersion;
    private readonly Dictionary<string, (string Signature, ListViewItem Item, FontIcon Current)> launcherRows = [];
    private int scrollRequestVersion;
    private double cornerRadius = 10;
    private (int Width, int Height, int Diameter) regionBounds;
    private CompositionRoundedRectangleGeometry? surfaceGeometry;
    private CompositionGeometricClip? surfaceClip;
    public bool IsShowing => showing;
    public LauncherWindow(AppController controller)
    {
        this.controller = controller; InitializeComponent(); hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        controller.LauncherSwitchFailed += ShowSwitchFailure;
        Versions.Loaded += (_, _) => QueueSelectedScroll(focus: true);
        Root.Loaded += (_, _) => UpdateSurfaceBounds();
        Root.SizeChanged += (_, _) => UpdateSurfaceBounds();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange || args.DidPositionChange) UpdateSurfaceBounds(); };
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
        presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, (nint)(style & ~0x00C40000L));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x27);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, args) => { if (!exiting) { args.Cancel = true; Dismiss(); } };
        Activated += (_, args) => { if (showing && args.WindowActivationState == WindowActivationState.Deactivated) Dismiss(); };
        var round = 1; _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        var noBorder = unchecked((int)0xfffffffe); _ = DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
    }
    public void Toggle() { if (showing) Dismiss(); else ShowLauncher(); }
    public void ShowLauncher()
    {
        presentationVersion++;
        Refresh();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var foregroundWindow = GetForegroundWindow();
        var monitor = MonitorFromWindow(foregroundWindow, 2); GetMonitorInfo(monitor, ref info);
        var scale = Math.Max(1, GetDpiForWindow(foregroundWindow == 0 ? hwnd : foregroundWindow) / 96.0);
        var width = Math.Min(info.Work.Right - info.Work.Left, (int)(480 * scale));
        var height = Math.Min(info.Work.Bottom - info.Work.Top, (int)((Math.Min(5, Math.Max(1, Versions.Items.Count)) * 68 + 104) * scale));
        AppWindow.MoveAndResize(new RectInt32(info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2, info.Work.Top + (info.Work.Bottom - info.Work.Top - height) / 2, width, height));
        UpdateSurfaceBounds();
        controller.BeginOverlay(); showing = true;
        AppWindow.Show(); Activate(); SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x43); SetForegroundWindow(hwnd);
        QueueSelectedScroll(focus: true);
    }
    private void Refresh()
    {
        ToolTipService.SetToolTip(Hint, null);
        var settings = controller.Settings;
        Glow.Visibility = settings.OverlayBackgroundStyle == "frosted" ? Visibility.Visible : Visibility.Collapsed;
        var dark = settings.Theme == "dark" || settings.Theme == "system" && SystemDark();
        var nativeDark = dark ? 1 : 0; _ = DwmSetWindowAttribute(hwnd, 20, ref nativeDark, sizeof(int));
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        SurfaceFrame.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        Inner.Padding = new Thickness(18);
        cornerRadius = Math.Clamp(settings.OverlayCornerRadius, 0, 40);
        BackdropLayer.CornerRadius = SurfaceFrame.CornerRadius = new CornerRadius(cornerRadius);
        var color = ParseColor(settings.OverlaySolidColor);
        var white = settings.OverlayTextColor == "white" || settings.OverlayTextColor == "auto" && (settings.OverlayBackgroundStyle == "image" || (settings.OverlayBackgroundStyle == "solid" ? color.R * .299 + color.G * .587 + color.B * .114 < 145 : dark));
        var foreground = new SolidColorBrush(white ? Colors.White : ParseColor("#202124"));
        foreach (var text in new[] { Heading, ShortcutLabel, Hint, EscapeHint }) text.Foreground = foreground;
        ApplyBackdrop(settings.OverlayBackgroundStyle == "frosted");
        Brush fill = settings.OverlayBackgroundStyle == "image" && controller.BackgroundPath is { } image
            ? new ImageBrush { ImageSource = new BitmapImage(new Uri(image)), Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(settings.OverlayBackgroundStyle == "solid" ? color : dark ? ParseColor("#25262B") : ParseColor("#F4F5F9"));
        fill.Opacity = SystemBackdrop is null ? 1 : Math.Clamp(settings.OverlayOpacity / 100, .2, 1);
        if (settings.OverlayBackgroundStyle == "frosted" && SystemBackdrop is not null) fill.Opacity *= .5;
        BackdropLayer.Background = fill;
        Heading.Text = controller.Text("切换 Java", "Switch Java", "切換 Java", "Java を切り替え"); ShortcutLabel.Text = settings.OverlayHotkey.Replace("+", " + ");
        Hint.Text = settings.SwitchMode == "instant" ? controller.Text("↑ ↓ 切换 · Enter 关闭", "↑ ↓ Switch · Enter Close", "↑ ↓ 切換 · Enter 關閉", "↑ ↓ 切り替え · Enter 閉じる") : controller.Text("↑ ↓ 选择 · Enter 确认", "↑ ↓ Select · Enter Apply", "↑ ↓ 選擇 · Enter 確認", "↑ ↓ 選択 · Enter 決定");
        EscapeHint.Text = controller.Text("Esc 关闭", "Esc Close", "Esc 關閉", "Esc 閉じる");
        var installations = settings.Installations.Where(x => x.IsValid && x.ShowInLauncher).ToList();
        var ids = installations.Select(x => x.Id).ToHashSet();
        foreach (var id in launcherRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            Versions.Items.Remove(launcherRows[id].Item); launcherRows.Remove(id);
        }
        var selectedIndex = -1;
        for (var index = 0; index < installations.Count; index++)
        {
            var java = installations[index];
            var signature = System.Text.Json.JsonSerializer.Serialize(new { java.DisplayName, java.Version, java.Vendor, java.Architecture, java.IconPath, white });
            if (!launcherRows.TryGetValue(java.Id, out var cached) || cached.Signature != signature)
            {
            var row = new Grid { Padding = new Thickness(12, 10, 12, 10), ColumnSpacing = 13, CornerRadius = new CornerRadius(8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (File.Exists(java.IconPath)) row.Children.Add(new Image { Width = 28, Height = 28, Source = new BitmapImage(new Uri(java.IconPath!)) });
            else row.Children.Add(new FontIcon { Glyph = "\uE7C3", FontSize = 22, Foreground = foreground });
            var details = new StackPanel { Spacing = 3 }; Grid.SetColumn(details, 1);
            details.Children.Add(new TextBlock { Text = java.DisplayName, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock { Text = $"{java.Version}  ·  {java.Vendor}  ·  {java.Architecture}", FontSize = 11, Opacity = .65, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis }); row.Children.Add(details);
            var check = new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = foreground }; Grid.SetColumn(check, 2); row.Children.Add(check);
            var item = cached.Item ?? new ListViewItem { Tag = java.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.Content = row; item.Margin = new Thickness(0, 0, 0, 8);
            launcherRows[java.Id] = cached = (signature, item, check);
            }
            if (index >= Versions.Items.Count || !ReferenceEquals(Versions.Items[index], cached.Item))
            {
                Versions.Items.Remove(cached.Item); Versions.Items.Insert(index, cached.Item);
            }
            cached.Current.Visibility = java.Id == controller.CurrentSelectedId ? Visibility.Visible : Visibility.Collapsed;
            if (java.Id == controller.CurrentSelectedId) selectedIndex = index;
        }
        if (Versions.Items.Count > 0) Versions.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        if (Versions.Items.Count == 0) Hint.Text = settings.Installations.Any(x => x.IsValid)
            ? controller.Text("请在版本设置中开启“在启动台中显示”", "Enable Show in launcher in version settings", "請在版本設定中開啟「在啟動台中顯示」", "バージョン設定でランチャーへの表示を有効にしてください")
            : controller.Text("请先在首页添加 Java", "Add Java on the home page", "請先在首頁新增 Java", "ホームで Java を追加してください");
        UpdateSurfaceBounds();
    }
    private void ApplyBackdrop(bool frosted)
    {
        if (frosted ? SystemBackdrop is DesktopAcrylicBackdrop : SystemBackdrop is ClearBackdrop) return;
        var previous = SystemBackdrop as ClearBackdrop;
        SystemBackdrop = null;
        previous?.Dispose();
        ClearBackdrop? transparent = null;
        try
        {
            if (frosted) SystemBackdrop = new DesktopAcrylicBackdrop();
            else SystemBackdrop = transparent = new ClearBackdrop(hwnd);
        }
        catch (Exception error)
        {
            SystemBackdrop = null;
            transparent?.Dispose();
            System.Diagnostics.Debug.WriteLine(error);
        }
    }
    private void UpdateSurfaceBounds()
    {
        if (exiting) return;
        if (Root.ActualWidth > 0 && Root.ActualHeight > 0)
        {
            var visual = ElementCompositionPreview.GetElementVisual(Root);
            surfaceGeometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
            surfaceGeometry.Size = new Vector2((float)Root.ActualWidth, (float)Root.ActualHeight);
            var radius = (float)Math.Min(cornerRadius, Math.Min(Root.ActualWidth, Root.ActualHeight) / 2);
            surfaceGeometry.CornerRadius = new Vector2(radius);
            surfaceClip ??= visual.Compositor.CreateGeometricClip(surfaceGeometry);
            visual.Clip = surfaceClip;
        }
        if (!GetWindowRect(hwnd, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;
        var diameter = (int)Math.Round(Math.Min(cornerRadius * GetDpiForWindow(hwnd) / 96.0 * 2, Math.Min(width, height)));
        var next = (width, height, diameter);
        if (regionBounds == next) return;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == 0) return;
        regionBounds = next;
        // A successful SetWindowRgn transfers region ownership to Windows.
        if (SetWindowRgn(hwnd, region, true) == 0) { regionBounds = default; _ = DeleteObject(region); }
    }
    private void QueueSelectedScroll(bool focus = false)
    {
        var presentation = presentationVersion;
        var request = ++scrollRequestVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (exiting || !showing || presentation != presentationVersion || request != scrollRequestVersion || !Versions.IsLoaded) return;
            // Rebuilt items must finish layout before ListView resolves their containers.
            Versions.UpdateLayout();
            var index = Versions.SelectedIndex;
            if (exiting || !showing || presentation != presentationVersion || request != scrollRequestVersion
                || index < 0 || index >= Versions.Items.Count || Versions.ActualHeight <= 0) return;
            var selected = Versions.Items[index];
            if (ReferenceEquals(Versions.SelectedItem, selected))
            {
                if (focus) Versions.Focus(FocusState.Programmatic);
                Versions.ScrollIntoView(selected);
            }
        });
    }
    private void ShowSwitchFailure(object? sender, string message)
    {
        if (exiting || !showing) return;
        Hint.Text = controller.Text("切换未完成，请在主界面查看详情。", "Switch incomplete. See the main window for details.", "切換未完成，請在主介面查看詳情。", "切り替えは未完了です。詳細はメイン画面で確認してください。");
        ToolTipService.SetToolTip(Hint, message);
    }
    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; Dismiss(); return; }
        if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            args.Handled = true; if (Versions.Items.Count == 0 || controller.Settings.SwitchMode == "instant" && pendingSwitch is { IsCompleted: false }) return;
            var next = Versions.SelectedIndex + (args.Key == VirtualKey.Up ? -1 : 1);
            next = controller.Settings.WrapSelection ? (next + Versions.Items.Count) % Versions.Items.Count : Math.Clamp(next, 0, Versions.Items.Count - 1);
            Versions.SelectedIndex = next; QueueSelectedScroll();
            if (controller.Settings.SwitchMode == "instant") await Apply(false);
        }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; await Apply(true); }
    }
    private void Versions_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (!showing) return;
        var item = Versions.Items.OfType<ListViewItem>().FirstOrDefault(candidate =>
            ReferenceEquals(candidate, args.ClickedItem) || ReferenceEquals(candidate.Content, args.ClickedItem));
        if (item is null) return;
        var version = presentationVersion;
        // Let the native click finish before hiding its window.
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (exiting || !showing || version != presentationVersion || !Versions.Items.Contains(item)) return;
            Versions.SelectedItem = item; await Apply(true);
        });
    }
    private async Task Apply(bool close)
    {
        if (Versions.SelectedItem is not ListViewItem { Tag: string id }) { if (close) Dismiss(); return; }
        if (close) Dismiss();
        if (pendingSwitch is { IsCompleted: false } existing && pendingTarget == id) { await existing; return; }
        var version = presentationVersion;
        pendingTarget = id;
        var operation = controller.SwitchFromLauncherAsync(id);
        pendingSwitch = operation;
        var succeeded = await operation;
        if (succeeded && !exiting && showing && presentationVersion == version && ReferenceEquals(pendingSwitch, operation))
        {
            ToolTipService.SetToolTip(Hint, null);
            Hint.Text = controller.Status;
        }
    }
    public void Dismiss() { if (!showing) return; showing = false; AppWindow.Hide(); controller.EndOverlay(); }
    public void Finish()
    {
        exiting = true; controller.LauncherSwitchFailed -= ShowSwitchFailure;
        ElementCompositionPreview.GetElementVisual(Root).Clip = null;
        surfaceClip?.Dispose(); surfaceGeometry?.Dispose();
        var transparent = SystemBackdrop as ClearBackdrop;
        SystemBackdrop = null; transparent?.Dispose();
        Close();
    }
    private static bool SystemDark() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
    private static Color ParseColor(string value) { try { return Color.FromArgb(255, Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16)); } catch { return Color.FromArgb(255, 246, 247, 251); } }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
