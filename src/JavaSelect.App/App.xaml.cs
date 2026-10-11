using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JavaSelect.App.Services;
using JavaSelect.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace JavaSelect.App;

public partial class App : Application
{
    private AppController? controller;
    private MainWindow? main;
    private LauncherWindow? launcher;
    private GlobalHotkeyService? hotkeys;
    private TrayService? tray;
    private Mutex? instance;
    private readonly CancellationTokenSource lifetime = new();
    private bool quitting, shutdownComplete;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            if (controller is null) return;
            controller.ReportError(args.Exception); args.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var command = Environment.GetCommandLineArgs();
            var smoke = command.Contains("--smoke");
            var directory = smoke ? Path.Combine(AppContext.BaseDirectory, "smoke-config") : null;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + (directory ?? "JavaSelect"))))[..20];
            var pipeName = "JavaSelect." + identity;
            if (command.Contains("--quit-for-update"))
            {
                Environment.ExitCode = await RequestExitForUpdateAsync(pipeName) ? 0 : 1;
                Exit(); return;
            }
            instance = new Mutex(true, @"Local\" + pipeName, out var created);
            if (!created)
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                try { await client.ConnectAsync(2000); await client.WriteAsync("open"u8.ToArray()); }
                catch (Exception error) when (error is IOException or TimeoutException) { }
                Exit(); return;
            }
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            controller = new AppController(dispatcher, directory, smoke);
            await controller.InitializeAsync();
            main = new MainWindow(controller);
            main.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
            main.AppWindow.Resize(new SizeInt32(1120, 800));
            main.AppWindow.Closing += (_, closing) =>
            {
                if (shutdownComplete) return;
                closing.Cancel = true;
                if (quitting) return;
                if (controller.Settings.CloseToTray && tray is not null) main.AppWindow.Hide();
                else Quit();
            };
            controller.ShowMainRequested += (_, _) => ShowMain();
            controller.UpdateInstallRequested += (_, path) => Quit(path);
            hotkeys = new GlobalHotkeyService(action => dispatcher.TryEnqueue(async () =>
            {
                if (quitting || controller.IsCapturingHotkey) return;
                try
                {
                    if (action == "launcher") { launcher ??= new LauncherWindow(controller); launcher.Toggle(); }
                    else await controller.SwitchAsync(action);
                }
                catch (Exception error) { controller.ReportError(error); ShowMain(); }
            }));
            hotkeys.Error += error => dispatcher.TryEnqueue(() => controller.ReportError(error));
            controller.RegisterHotkeysAsync = bindings => Task.Run(() => hotkeys.Update(bindings));
            try { await controller.RegisterCurrentHotkeysAsync(); }
            catch (Exception error) { controller.ReportError(error); }
            CreateTray(dispatcher);
            controller.PreferencesChanged += (_, _) => CreateTray(dispatcher);
            _ = ListenAsync(pipeName, dispatcher);
            // Register only after notification actions can safely use the window and loaded caches.
            var notificationLaunch = controller.StartNotifications();
            if ((!notificationLaunch && !command.Contains("--background") && !controller.Settings.StartInTray) || tray is null) ShowMain();
            controller.StartMonitoring();
            if (smoke) await controller.ScanAsync();
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "JavaSelect-startup.log"), error.ToString()); } catch { }
            MessageBox(0, "JavaSelect 无法启动。错误详情已保存到临时目录中的 JavaSelect-startup.log。", "JavaSelect", 0x10);
            Quit();
        }
    }

    private void CreateTray(DispatcherQueue dispatcher)
    {
        if (controller is null || quitting) return;
        try
        {
            var openText = controller.Text("打开 JavaSelect", "Open JavaSelect", "開啟 JavaSelect", "JavaSelect を開く");
            var exitText = controller.Text("退出", "Exit", "結束", "終了");
            if (tray is not null) { tray.UpdateText("JavaSelect", openText, exitText); return; }
            tray = new TrayService(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"), "JavaSelect",
                () => dispatcher.TryEnqueue(ShowMain), () => dispatcher.TryEnqueue(Quit),
                openText, exitText);
            tray.Error += error => dispatcher.TryEnqueue(() => controller.ReportError(error));
        }
        catch (Exception error) { controller.ReportError(error); }
    }

    private async Task ListenAsync(string name, DispatcherQueue dispatcher)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var buffer = new byte[32]; var count = 0;
                while (count < buffer.Length)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(count), deadline.Token);
                    if (read == 0) break;
                    count += read;
                }
                var command = Encoding.UTF8.GetString(buffer, 0, count);
                if (command == "open") dispatcher.TryEnqueue(ShowMain);
                else if (command == "quit-for-update") dispatcher.TryEnqueue(() => Quit());
            }
            catch (OperationCanceledException) { if (lifetime.IsCancellationRequested) return; }
            catch (IOException error) { controller?.ReportError(error); return; }
        }
    }

    private void ShowMain()
    {
        if (main is null || quitting) return;
        main.AppWindow.Show();
        if (main.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Restore();
        main.Activate(); SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(main));
    }

    private void Quit() => Quit(null);
    private async void Quit(string? installerPath)
    {
        if (quitting) return; quitting = true;
        lifetime.Cancel();
        FileStream? installerLock = null;
        try
        {
            if (controller is not null) await controller.ShutdownAsync();
            if (installerPath is not null)
            {
                installerLock = controller is null ? null : await controller.LockUpdateInstallerAsync(installerPath);
                if (installerLock is null) throw new IOException("The update installer could not be verified after shutdown.");
            }
        }
        catch (Exception error)
        {
            controller?.ReportError(error);
            if (installerPath is not null)
                MessageBox(0, controller?.Text("无法安全启动更新，请重新打开 JavaSelect 后重试。", "Could not safely start the update. Reopen JavaSelect and try again.", "無法安全啟動更新，請重新開啟 JavaSelect 後重試。", "更新を安全に開始できませんでした。JavaSelect を再度開いてお試しください。") ?? "Update failed.", "JavaSelect", 0x10);
            installerPath = null;
        }
        finally
        {
            foreach (var resource in new IDisposable?[] { hotkeys, tray, controller, instance })
                try { resource?.Dispose(); } catch (Exception error) { controller?.ReportError(error); }
            shutdownComplete = true;
            launcher?.Finish(); main?.Close();
            if (installerPath is not null)
            {
                try { Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installerPath) }); }
                catch (Exception error)
                {
                    controller?.ReportError(error);
                    var message = controller?.Text("无法启动安装器，请手动打开：", "Could not start the installer. Open it manually:", "無法啟動安裝程式，請手動開啟：", "インストーラーを起動できませんでした。手動で開いてください：");
                    MessageBox(0, message + Environment.NewLine + installerPath, "JavaSelect", 0x10);
                }
            }
            installerLock?.Dispose();
            Exit();
        }
    }

    private static async Task<bool> RequestExitForUpdateAsync(string pipeName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try
        {
            using var running = Mutex.OpenExisting(@"Local\" + pipeName);
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(3000, timeout.Token);
                await client.WriteAsync("quit-for-update"u8.ToArray(), timeout.Token);
            }
            return await Task.Run(() =>
            {
                try
                {
                    var result = WaitHandle.WaitAny([running, timeout.Token.WaitHandle]);
                    if (result != 0) return false;
                }
                catch (AbandonedMutexException) { }
                running.ReleaseMutex(); return true;
            });
        }
        catch (WaitHandleCannotBeOpenedException) { return true; }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")] private static extern int MessageBox(nint window, string text, string title, uint type);
}
