using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JavaSelect.App.Services;
using JavaSelect.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;

namespace JavaSelect.App;

public sealed partial class AppController : IDisposable
{
    private readonly ConfigStore store;
    private readonly JavaDiscoveryService discovery = new();
    private readonly EnvironmentSwitchService switcher;
    private readonly CommunityService community;
    private readonly NotificationService notifications = new();
    private readonly DispatcherQueue dispatcher;
    private readonly SemaphoreSlim operations = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathRepairAttemptPolicy repairAttempts = new();
    private EnvironmentMonitor? monitor;
    private bool startupApplied, startupStateKnown, terminalApplied, checkPending, disposed, overlayOpen, stopping;
    private int queuedOperations;
    private AppSettings committedSettings = new();
    private IReadOnlyList<HotkeyBinding> appliedHotkeys = [];
    private Task? environmentTask, retryTask, monitorStartTask, updatesTask, aboutTask, shutdownTask;
    private string[] monitoredRoots = [];
    private string? repairTargetId;
    private string? overlayInitial;
    private bool notificationWarningShown;
    public bool IsReadOnly { get; }
    public bool IsCapturingHotkey { get; set; }
    public AppSettings Settings { get; private set; } = new();
    public string AppVersion => BuildInfo.Version;
    public string Status { get; private set; } = "";
    public bool IsBusy { get; private set; }
    public bool IsScanning { get; private set; }
    public bool IsLoadingAbout { get; private set; }
    public bool IsCheckingUpdates { get; private set; }
    public string? ConflictPath { get; private set; }
    public string? CurrentSelectedId { get; private set; }
    public string CurrentJavaHome { get; private set; } = "";
    public string? EffectiveJavaPath { get; private set; }
    public IReadOnlyList<JavaInstallation> Candidates { get; private set; } = [];
    public IList<Contributor> Contributors { get; private set; } = new List<Contributor>();
    public string QuoteText { get; private set; } = "";
    public string QuoteSource { get; private set; } = "";
    public string UpdateStatus { get; private set; } = "";
    public string? ReleaseUrl { get; private set; }
    public string ConfigurationDirectory => store.DirectoryPath;
    public event EventHandler? Changed;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? PreferencesChanged;
    public event EventHandler? ShowMainRequested;
    internal event EventHandler<string>? LauncherSwitchFailed;
    public Func<IReadOnlyList<HotkeyBinding>, Task>? RegisterHotkeysAsync { get; set; }

    public AppController(DispatcherQueue dispatcher, string? directory = null, bool readOnly = false)
    {
        this.dispatcher = dispatcher; IsReadOnly = readOnly;
        store = new ConfigStore(directory); switcher = new EnvironmentSwitchService(store);
        community = new CommunityService(Path.Combine(store.DirectoryPath, "cache"));
        updater = new UpdateService(store.DirectoryPath); installedApplication = updater.GetInstallation();
        notifications.ActionInvoked += (_, action) => dispatcher.TryEnqueue(async () => await HandleNotificationActionAsync(action));
    }

    internal async Task HandleNotificationActionAsync(NotificationAction action)
    {
        if (stopping || !NotificationService.IsValidAction(action)) return;
        try
        {
            if (action.Action == "open") { ShowMainRequested?.Invoke(this, EventArgs.Empty); return; }
            var item = Candidates.FirstOrDefault(x => x.Id == action.CandidateId);
            if (item is null) return;
            if (action.Action == "add") await ImportCandidatesAsync([item.Id]);
            else if (action.Action == "ignore") await RunOperationAsync(async () => { seen.Add(Signature(item)); await SaveSeenAsync(); });
        }
        catch (Exception error) { ReportError(error); }
    }

    public async Task InitializeAsync()
    {
        Settings = await store.LoadAsync(lifetime.Token);
        committedSettings = CloneSettings(Settings);
        if (!IsReadOnly)
        {
            var startup = new StartupService();
            try { await Task.Run(() => startup.ReconcileAtLaunch(Settings.StartAtLogin, Environment.ProcessPath!)); }
            catch (Exception error)
            {
                Log(error);
                SetStatus(Text($"无法应用开机启动设置：{error.Message}", $"Could not apply the start-at-sign-in setting: {error.Message}",
                    $"無法套用開機啟動設定：{error.Message}", $"自動起動の設定を適用できません：{error.Message}"));
            }
            try
            {
                startupApplied = await Task.Run(() => startup.IsEnabled(Environment.ProcessPath!));
                startupStateKnown = true;
            }
            catch (Exception error)
            {
                Log(error);
                SetStatus(Text("无法检查开机启动，请启用 Windows 任务计划程序服务后重试。", "Could not check startup. Enable the Windows Task Scheduler service and retry.",
                    "無法檢查開機啟動，請啟用 Windows 工作排程器服務後重試。", "自動起動を確認できません。Windows タスク スケジューラ サービスを有効にして再試行してください。"));
            }
        }
        terminalApplied = !IsReadOnly && File.Exists(Path.Combine(store.DirectoryPath, "terminal-sync.enabled"));
        var candidateFile = Path.Combine(store.DirectoryPath, "candidates.json");
        if (File.Exists(candidateFile))
            try { Candidates = JsonSerializer.Deserialize<List<JavaInstallation>>(await File.ReadAllTextAsync(candidateFile)) ?? []; } catch (JsonException e) { Log(e); }
        await Task.Run(() => { foreach (var java in Settings.Installations) java.IconPath = IconCache.GetJavaIcon(java, store.DirectoryPath); }, lifetime.Token);
        committedSettings = CloneSettings(Settings);
        await RefreshCurrentAsync();
        if (!IsReadOnly)
        {
            var path = Path.Combine(store.DirectoryPath, "seen.json");
            if (File.Exists(path)) try { foreach (var item in JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path)) ?? []) seen.Add(item); } catch (JsonException e) { Log(e); }
        }
    }

    internal async Task<bool> StartNotificationsAsync()
    {
        if (IsReadOnly || stopping) return false;
        try { await notifications.InitializeAsync(); return notifications.HandleLaunchActivation(); }
        catch (Exception error) { Log(error); return false; }
    }

    public void StartMonitoring()
    {
        if (IsReadOnly || stopping || monitorStartTask is not null) return;
        monitorStartTask = StartMonitoringCoreAsync();
        if (Settings.AutoUpdate) _ = QuietUpdatesAsync();
    }

    private async Task StartMonitoringCoreAsync()
    {
        try
        {
            var installations = committedSettings.Installations.Select(x => x.Path).ToArray();
            var roots = MonitoringRoots(installations);
            var created = await Task.Run(() => new EnvironmentMonitor(
                () => dispatcher.TryEnqueue(() => { if (!stopping) _ = CheckEnvironmentAsync(); }), javaRoots: roots));
            if (stopping) { await Task.Run(created.Dispose); return; }
            monitor = created;
            monitoredRoots = roots;
            monitor.Error += error => dispatcher.TryEnqueue(() => { if (!stopping) ReportError(error); });
            _ = CheckEnvironmentAsync();
        }
        catch (Exception error) { if (!stopping) ReportError(error); }
    }

    public Task ScanAsync() => RunOperationAsync(async () =>
    {
        IsScanning = true; Changed?.Invoke(this, EventArgs.Empty);
        try { await ScanCoreAsync(reportStatus: true); }
        finally { IsScanning = false; Changed?.Invoke(this, EventArgs.Empty); }
    });

    private async Task ScanCoreAsync(bool reportStatus = false)
    {
        if (reportStatus) SetStatus(Text("正在扫描 Java…", "Scanning Java…", "正在掃描 Java…", "Java を検索中…"));
        var draft = CloneSettings(committedSettings);
        var results = await discovery.ScanAsync(draft.Installations.Select(x => x.Path), lifetime.Token);
        await Task.Run(() =>
        {
            foreach (var java in results)
            {
                java.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Signature(java))))[..32];
                java.IconPath = IconCache.GetJavaIcon(java, store.DirectoryPath);
            }
        }, lifetime.Token);
        foreach (var saved in draft.Installations)
        {
            var found = results.FirstOrDefault(x => SamePath(x.Path, saved.Path));
            if (found is not null) CopyInstallation(found, saved, preserveIdentity: true, preserveEdits: true);
            else { saved.IsValid = false; saved.Error = "The Java installation is unavailable."; }
        }
        var candidates = results.Where(x => !draft.Installations.Any(y => SamePath(y.Path, x.Path))).ToArray();
        await store.SaveAsync(draft, lifetime.Token);
        Commit(draft, preferences: false);
        Candidates = candidates;
        await SaveCandidatesAsync();
        await RefreshCurrentAsync();
        await RefreshMonitoringRootsAsync();
        if (reportStatus) SetStatus(Text($"找到 {results.Count} 个 Java 安装", $"Found {results.Count} Java installations", $"找到 {results.Count} 個 Java 安裝", $"Java が {results.Count} 件見つかりました"));
    }

    public Task AddAsync(string path) => RunOperationAsync(() => AddCoreAsync(path));

    private async Task AddCoreAsync(string path)
    {
        var java = await discovery.ProbeAsync(path, lifetime.Token);
        if (java?.IsValid != true) throw new IOException(Text("请选择包含 bin\\java.exe 的有效 Java 安装目录。", "Choose a valid Java folder containing bin\\java.exe.", "請選擇包含 bin\\java.exe 的有效 Java 目錄。", "bin\\java.exe を含む有効な Java フォルダーを選択してください。"));
        if (committedSettings.Installations.Any(x => SamePath(x.Path, java.Path))) return;
        java.IconPath = await Task.Run(() => IconCache.GetJavaIcon(java, store.DirectoryPath), lifetime.Token);
        var draft = CloneSettings(committedSettings); draft.Installations.Add(java);
        await store.SaveAsync(draft, lifetime.Token);
        Commit(draft, preferences: false);
        Candidates = Candidates.Where(x => !Settings.Installations.Any(y => SamePath(y.Path, x.Path))).ToArray();
        await SaveCandidatesAsync(); await RefreshCurrentAsync(); await RefreshMonitoringRootsAsync();
    }

    public Task ImportCandidatesAsync(IEnumerable<string> ids)
    {
        var requested = ids.ToArray();
        return RunOperationAsync(async () =>
        {
            foreach (var id in requested)
            {
                var candidate = Candidates.FirstOrDefault(x => x.Id == id); if (candidate is null) continue;
                var verified = await discovery.ProbeAsync(candidate.Path, lifetime.Token);
                if (verified?.IsValid == true && verified.Fingerprint == candidate.Fingerprint) await AddCoreAsync(candidate.Path);
            }
        });
    }

    public async Task SwitchAsync(string id) { await ApplySelectionAsync(id, false, null); }
    internal async Task<bool> SwitchFromLauncherAsync(string id)
    {
        if (stopping) return false;
        try
        {
            var success = await ApplySelectionAsync(id, false, null);
            if (!success && !IsReadOnly && !stopping) PresentLauncherFailure();
            return success;
        }
        catch (OperationCanceledException) when (stopping) { return false; }
        catch (Exception error)
        {
            ReportError(error);
            if (!stopping) PresentLauncherFailure();
            return false;
        }
    }
    private void PresentLauncherFailure()
    {
        if (overlayOpen && LauncherSwitchFailed is { } showFailure) showFailure(this, Status);
        else ShowMainRequested?.Invoke(this, EventArgs.Empty);
    }
    private Task<bool> ApplySelectionAsync(string id, bool recovery, string? expected) => RunOperationAsync<bool>(() => ApplySelectionCoreAsync(id, recovery, expected));
    private async Task<bool> ApplySelectionCoreAsync(string id, bool recovery, string? expected, CancellationToken? operationToken = null, bool allowAutomaticRepair = true)
    {
            if (IsReadOnly) { SetStatus(Text("当前为只读测试，未修改系统环境。", "Read-only test: the system environment was not changed.", "目前為唯讀測試，未修改系統環境。", "読み取り専用テストのため環境変数は変更していません。")); return false; }
            var draft = CloneSettings(committedSettings);
            if (recovery && (!draft.KeepCurrentJavaSelection || id != draft.LastSelectedId || expected != draft.LastSelectedFingerprint)) return false;
            var java = draft.Installations.FirstOrDefault(x => x.Id == id);
            if (java is null) { SetStatus(Text("此版本已被移除，请刷新列表后重试。", "This version was removed. Refresh the list and try again.", "此版本已被移除，請重新整理清單後再試。", "このバージョンは削除されました。一覧を更新して再試行してください。")); return false; }
            repairTargetId = id;
            var previous = CurrentSelectedId;
            var result = await switcher.SwitchAsync(java, draft, operationToken ?? lifetime.Token, expectedFingerprint: expected);
            string? repairMessage = null;
            if (allowAutomaticRepair && !result.Success && result.ConflictPath is not null)
                (result, repairMessage) = await TryRepairAndRetrySelectionAsync(java, draft, result, recovery, operationToken ?? lifetime.Token);
            ConflictPath = result.ConflictPath;
            if (result.Success) { Commit(draft, preferences: false); repairTargetId = null; repairAttempts.SelectionSucceeded(); }
            if (!stopping) await RefreshCurrentAsync();
            if (!result.Success)
            {
                SetStatus(repairMessage ?? (result.ConflictPath is { } path
                    ? Text($"系统 PATH 优先命中 {path}。请调整环境变量后重试。", $"The system PATH resolves to {path}. Adjust Environment Variables and retry.", $"系統 PATH 優先使用 {path}，請調整環境變數後重試。", $"PATH は {path} を優先しています。環境変数を調整して再試行してください。")
                    : result.Error ?? Text("切换失败", "Switch failed", "切換失敗", "切り替え失敗")));
                return false;
            }
            SetStatus(Switched(java));
            if (!stopping && (recovery ? Settings.RestoreNotifications : !overlayOpen && Settings.SwitchNotifications && previous != id))
                await NotifyAsync(recovery ? Text("Java 环境已恢复", "Java environment restored", "Java 環境已還原", "Java 環境を復元") : "JavaSelect", Status);
            return true;
    }

    public Task RemoveAsync(string id) => RunOperationAsync(async () =>
    {
        if (id == committedSettings.LastSelectedId) throw new InvalidOperationException(Text("请先切换到其他版本。", "Switch to another version first.", "請先切換至其他版本。", "先に別のバージョンに切り替えてください。"));
        var draft = CloneSettings(committedSettings); draft.Installations.RemoveAll(x => x.Id == id);
        await SaveCoreAsync(draft, preferences: false);
        await RefreshMonitoringRootsAsync();
    });

    public Task SaveAsync()
    {
        var requested = CloneSettings(Settings);
        var baseline = CloneSettings(committedSettings);
        return RunOperationAsync(async () =>
        {
            var draft = CloneSettings(committedSettings);
            CopyPreferences(requested, baseline, draft);
            // Switching to manual starts from the order the user was just looking at.
            if (draft.SortMode == InstallationOrder.Manual && committedSettings.SortMode != InstallationOrder.Manual)
                draft.Installations = InstallationOrder.Sort(draft.Installations, committedSettings.SortMode).ToList();
            await SaveCoreAsync(draft, preferences: true);
        });
    }

    /// <summary>Installations in the order chosen by <see cref="AppSettings.SortMode"/>; use this for every list the user sees.</summary>
    public IReadOnlyList<JavaInstallation> OrderedInstallations => InstallationOrder.Sort(Settings.Installations, Settings.SortMode);
    public bool IsManualOrder => Settings.SortMode == InstallationOrder.Manual;

    /// <summary>Moves one installation up (-1) or down (+1) in the manual order.</summary>
    public Task MoveInstallationAsync(string id, int offset) => RunOperationAsync(async () =>
    {
        if (committedSettings.SortMode != InstallationOrder.Manual) return;
        var draft = CloneSettings(committedSettings);
        var index = draft.Installations.FindIndex(x => x.Id == id);
        var target = index + Math.Sign(offset);
        if (index < 0 || target < 0 || target >= draft.Installations.Count || offset == 0) return;
        (draft.Installations[index], draft.Installations[target]) = (draft.Installations[target], draft.Installations[index]);
        await SaveCoreAsync(draft, preferences: false);
    });

    public Task ResetAppearanceAsync() => RunOperationAsync(async () =>
    {
        var draft = CloneSettings(committedSettings);
        draft.ResetAppearance();
        await SaveCoreAsync(draft, preferences: true);
    });

    public async Task RegisterCurrentHotkeysAsync()
    {
        var bindings = HotkeyBindings(committedSettings);
        if (RegisterHotkeysAsync is not null) await RegisterHotkeysAsync(bindings);
        appliedHotkeys = bindings;
    }

    private async Task SaveCoreAsync(AppSettings draft, bool preferences)
    {
        var oldStartup = startupApplied; var oldTerminal = terminalApplied;
        var oldHotkeys = appliedHotkeys;
        var startupAttempted = false; var terminalAttempted = false; var hotkeysChanged = false;
        try
        {
            var bindings = HotkeyBindings(draft);
            if (RegisterHotkeysAsync is not null && !bindings.SequenceEqual(appliedHotkeys))
            {
                await RegisterHotkeysAsync(bindings); appliedHotkeys = bindings; hotkeysChanged = true;
            }
            // Only the toggle itself may take startup over from another installation; re-syncing during unrelated saves must not.
            var startupChoice = draft.StartAtLogin != committedSettings.StartAtLogin;
            if (!IsReadOnly && (startupChoice || startupStateKnown && draft.StartAtLogin != startupApplied))
            {
                startupAttempted = true;
                await Task.Run(() => new StartupService().SetEnabled(draft.StartAtLogin, Environment.ProcessPath!, takeOver: startupChoice)); startupApplied = draft.StartAtLogin; startupStateKnown = true;
            }
            if (!IsReadOnly && draft.AutomaticTerminalSync != terminalApplied)
            {
                terminalAttempted = true;
                await Task.Run(() => TerminalIntegration.SetEnabled(draft.AutomaticTerminalSync, store.DirectoryPath)); terminalApplied = draft.AutomaticTerminalSync;
            }
            await store.SaveAsync(draft, lifetime.Token);
            Commit(draft, preferences);
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            try { if (terminalAttempted) { await Task.Run(() => TerminalIntegration.SetEnabled(oldTerminal, store.DirectoryPath)); terminalApplied = oldTerminal; } }
            catch (Exception error) { terminalApplied = File.Exists(Path.Combine(store.DirectoryPath, "terminal-sync.enabled")); failures.Add(error); Log(error); }
            try { if (startupAttempted) { await Task.Run(() => new StartupService().SetEnabled(oldStartup, Environment.ProcessPath!, takeOver: false)); startupApplied = oldStartup; } }
            catch (Exception error) { failures.Add(error); Log(error); }
            try { if (hotkeysChanged && RegisterHotkeysAsync is not null) { await RegisterHotkeysAsync(oldHotkeys); appliedHotkeys = oldHotkeys; } }
            catch (Exception error) { failures.Add(error); Log(error); }
            CopySettings(committedSettings, Settings, preferences: true);
            if (failures.Count > 1) throw new AggregateException("Settings could not be saved or completely rolled back.", failures);
            throw;
        }
        if (!stopping) PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string?> PickBackgroundAsync(nint hwnd)
    {
        var picker = new FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync(); if (file is null) return null;
        return await RunOperationAsync(async () =>
        {
            var draft = CloneSettings(committedSettings);
            var path = await store.ImportBackgroundAsync(file.Path, draft, async (copy, token) =>
            {
                var copied = await StorageFile.GetFileFromPathAsync(copy); using var stream = await copied.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if (decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 40000000) throw new IOException("Maximum image size: 8192 × 8192 px / 40 MP.");
                _ = await decoder.GetPixelDataAsync(); token.ThrowIfCancellationRequested();
            }, lifetime.Token);
            CommitBackground(draft);
            return path;
        });
    }
    public Task RemoveBackgroundAsync() => RunOperationAsync(async () =>
    {
        var draft = CloneSettings(committedSettings);
        await store.RemoveBackgroundAsync(draft, lifetime.Token);
        CommitBackground(draft);
    });
    private void CommitBackground(AppSettings draft)
    {
        Commit(draft, preferences: false);
        Settings.OverlayImagePath = draft.OverlayImagePath; Settings.OverlayImageName = draft.OverlayImageName;
        Settings.OverlayBackgroundStyle = draft.OverlayBackgroundStyle;
        if (!stopping) PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }
    internal string? BackgroundPath => store.ResolveBackgroundPath(Settings.OverlayImagePath);
    internal void BeginOverlay() { overlayOpen = true; overlayInitial = CurrentSelectedId; }
    internal void EndOverlay()
    {
        if (!stopping && overlayOpen && !IsReadOnly && Settings.SwitchNotifications && CurrentSelectedId != overlayInitial)
        {
            var java = Settings.Installations.FirstOrDefault(x => x.Id == CurrentSelectedId); if (java is not null) _ = NotifyAsync("JavaSelect", Switched(java));
        }
        overlayOpen = false;
    }
    private string Switched(JavaInstallation java) => Text($"已切换至 {java.DisplayName}，新终端生效。", $"Switched to {java.DisplayName}. Open a new terminal.", $"已切換至 {java.DisplayName}，新終端生效。", $"{java.DisplayName} に切り替えました。新しいターミナルで有効です。");

    public Task CheckUpdatesAsync()
    {
        if (stopping || IsDownloadingUpdate) return Task.CompletedTask;
        return updatesTask is { IsCompleted: false } ? updatesTask : updatesTask = CheckUpdatesCoreAsync();
    }
    private async Task CheckUpdatesCoreAsync()
    {
        IsCheckingUpdates = true; Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var release = await community.GetReleaseAsync(ProjectInfo.Repository, AppVersion, lifetime.Token);
            availableRelease = release;
            if (downloadedInstaller?.Version != (release is null ? null : ReleaseValidation.NormalizeVersion(release.Version))) downloadedInstaller = null;
            ReleaseUrl = release?.Url;
            UpdateStatus = release is null ? Text("暂无正式发布版本", "No published release", "尚無正式版本", "正式リリースはありません") : release.IsNew ? Text($"发现新版本 {release.Version}", $"Version {release.Version} available", $"發現新版本 {release.Version}", $"新バージョン {release.Version}") : Text("已是最新版本", "You're up to date", "已是最新版本", "最新バージョンです");
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error)
        {
            Log(error); UpdateStatus = UpdateText("updateCheckFailed");
        }
        finally { IsCheckingUpdates = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private async Task QuietUpdatesAsync() { try { await CheckUpdatesAsync(); } catch (Exception e) { Log(e); } }
    public Task RefreshAboutAsync()
    {
        if (stopping) return Task.CompletedTask;
        return aboutTask is { IsCompleted: false } ? aboutTask : aboutTask = RefreshAboutCoreAsync();
    }
    private async Task RefreshAboutCoreAsync()
    {
        IsLoadingAbout = true; Changed?.Invoke(this, EventArgs.Empty);
        var errors = new List<Exception>();
        async Task LoadContributors()
        {
            try { Contributors = (await community.GetContributorsAsync(ProjectInfo.Repository, lifetime.Token)).ToList(); }
            catch (OperationCanceledException) when (stopping) { }
            catch (Exception error) { errors.Add(error); }
        }
        async Task LoadQuote()
        {
            try { var quote = await community.GetQuoteAsync(lifetime.Token); QuoteText = quote.Text; QuoteSource = quote.Source; }
            catch (OperationCanceledException) when (stopping) { }
            catch (Exception error) { errors.Add(error); }
        }
        try
        {
            await Task.WhenAll(LoadContributors(), LoadQuote());
            if (!stopping && errors.Count > 0)
            {
                foreach (var error in errors) Log(error);
                SetStatus(Text("部分在线内容加载失败，请稍后刷新。", "Some online content could not load. Try refreshing later.", "部分線上內容載入失敗，請稍後重新整理。", "一部のオンライン情報を取得できませんでした。後で更新してください。"));
            }
        }
        finally { IsLoadingAbout = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private Task CheckEnvironmentAsync()
    {
        if (stopping || IsReadOnly) return Task.CompletedTask;
        if (environmentTask is { IsCompleted: false }) { checkPending = true; return environmentTask; }
        return environmentTask = CheckEnvironmentCoreAsync();
    }
    private async Task CheckEnvironmentCoreAsync()
    {
        try
        {
            var snapshot = CloneSettings(committedSettings);
            if (snapshot.KeepCurrentJavaSelection && snapshot.LastSelectedId is { } id && snapshot.LastSelectedFingerprint is { Length: > 0 } fingerprint)
            {
                var java = snapshot.Installations.FirstOrDefault(x => x.Id == id);
                if (java is not null && !await switcher.IsCurrentAsync(java, lifetime.Token))
                {
                    var env = new WindowsEnvironmentStore(); var before = await env.ReadAsync(lifetime.Token);
                    await Task.Delay(5000, lifetime.Token); var after = await env.ReadAsync(lifetime.Token);
                    if (before.UserJavaHome == after.UserJavaHome && before.UserPath == after.UserPath && before.MachineJavaHome == after.MachineJavaHome && before.MachinePath == after.MachinePath)
                        await ApplySelectionAsync(id, true, fingerprint);
                    else { checkPending = true; return; }
                }
            }
            await RunOperationAsync(async () =>
            {
                await ScanCoreAsync();
                var baseline = File.Exists(Path.Combine(store.DirectoryPath, "seen.json"));
                foreach (var java in Candidates)
                    if (seen.Add(Signature(java)) && baseline && committedSettings.DiscoveryNotifications && !stopping)
                        await NotifyAsync(Text("发现新的 Java", "New Java found", "發現新的 Java", "新しい Java を検出"), java.DisplayName + "\n" + java.Path, java.Id);
                await SaveSeenAsync();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log(e); }
        finally
        {
            if (checkPending && !stopping) { checkPending = false; if (retryTask is not { IsCompleted: false }) retryTask = RetryCheckAsync(); }
        }
    }
    private async Task RetryCheckAsync() { try { await Task.Delay(10000, lifetime.Token); dispatcher.TryEnqueue(() => { if (!stopping) _ = CheckEnvironmentAsync(); }); } catch (OperationCanceledException) { } }
    private Task SaveSeenAsync() => SaveCacheAsync("seen.json", seen);
    private Task SaveCandidatesAsync() => SaveCacheAsync("candidates.json", Candidates);
    private async Task SaveCacheAsync<T>(string name, T value)
    {
        var path = Path.Combine(store.DirectoryPath, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Task RunOperationAsync(Func<Task> operation) => RunOperationAsync(async () => { await operation(); return true; });
    private async Task<T> RunOperationAsync<T>(Func<Task<T>> operation)
    {
        if (stopping) throw new OperationCanceledException(lifetime.Token);
        queuedOperations++; IsBusy = true; Changed?.Invoke(this, EventArgs.Empty);
        var entered = false;
        try
        {
            await operations.WaitAsync(lifetime.Token); entered = true;
            lifetime.Token.ThrowIfCancellationRequested();
            return await operation();
        }
        finally
        {
            if (entered) operations.Release();
            IsBusy = --queuedOperations > 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static readonly PropertyInfo[] SettingProperties = typeof(AppSettings).GetProperties().Where(x => x.CanWrite && x.Name != nameof(AppSettings.Installations)).ToArray();
    private static readonly PropertyInfo[] InstallationProperties = typeof(JavaInstallation).GetProperties().Where(x => x.CanWrite).ToArray();
    private static bool IsSelectionProperty(string name) => name is nameof(AppSettings.SchemaVersion) or nameof(AppSettings.LastSelectedId) or nameof(AppSettings.LastSelectedFingerprint) or nameof(AppSettings.ManagedPathEntry);
    private static AppSettings CloneSettings(AppSettings source)
    {
        var copy = new AppSettings();
        CopySettings(source, copy, preferences: true);
        return copy;
    }
    private static void CopyPreferences(AppSettings source, AppSettings baseline, AppSettings target)
    {
        foreach (var property in SettingProperties.Where(x => !IsSelectionProperty(x.Name)))
            if (!Equals(property.GetValue(source), property.GetValue(baseline))) property.SetValue(target, property.GetValue(source));
        foreach (var installation in target.Installations)
            if (source.Installations.FirstOrDefault(x => x.Id == installation.Id) is { } requested
                && baseline.Installations.FirstOrDefault(x => x.Id == installation.Id) is { } previous)
            {
                if (requested.Alias != previous.Alias) installation.Alias = requested.Alias;
                if (requested.Hotkey != previous.Hotkey) installation.Hotkey = requested.Hotkey;
                if (requested.ShowInLauncher != previous.ShowInLauncher) installation.ShowInLauncher = requested.ShowInLauncher;
            }
    }
    private static void CopyInstallation(JavaInstallation source, JavaInstallation target, bool preserveIdentity = false, bool preserveEdits = false)
    {
        foreach (var property in InstallationProperties)
        {
            if (preserveIdentity && property.Name == nameof(JavaInstallation.Id)) continue;
            if (preserveEdits && property.Name is nameof(JavaInstallation.Alias) or nameof(JavaInstallation.Hotkey) or nameof(JavaInstallation.ShowInLauncher)) continue;
            property.SetValue(target, property.GetValue(source));
        }
    }
    private static void CopySettings(AppSettings source, AppSettings target, bool preferences)
    {
        foreach (var property in SettingProperties.Where(x => preferences || IsSelectionProperty(x.Name))) property.SetValue(target, property.GetValue(source));
        var existing = target.Installations.ToDictionary(x => x.Id, StringComparer.Ordinal);
        target.Installations.Clear();
        foreach (var installation in source.Installations)
        {
            var retained = existing.TryGetValue(installation.Id, out var previous);
            var targetInstallation = retained ? previous! : new JavaInstallation();
            CopyInstallation(installation, targetInstallation, preserveEdits: retained && !preferences);
            target.Installations.Add(targetInstallation);
        }
    }
    private void Commit(AppSettings draft, bool preferences)
    {
        committedSettings = CloneSettings(draft);
        CopySettings(draft, Settings, preferences);
    }
    private static IReadOnlyList<HotkeyBinding> HotkeyBindings(AppSettings settings)
    {
        var bindings = new List<HotkeyBinding> { new("launcher", GlobalHotkeyService.NormalizeShortcut(settings.OverlayHotkey)) };
        bindings.AddRange(settings.Installations.Where(x => !string.IsNullOrWhiteSpace(x.Hotkey))
            .Select(x => new HotkeyBinding(x.Id, GlobalHotkeyService.NormalizeShortcut(x.Hotkey))));
        return bindings;
    }
    private static string[] MonitoringRoots(IEnumerable<string> installations)
    {
        var programRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0).ToArray();
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var broad = new HashSet<string>(programRoots.Append(user).Append(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var path in installations)
        {
            try
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var parent = Path.GetDirectoryName(normalized);
                roots.Add(parent is not null && !broad.Contains(parent) && !SamePath(parent, Path.GetPathRoot(parent)!) ? parent : normalized);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        }
        foreach (var program in programRoots)
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "BellSoft", "Zulu", "Semeru" }) roots.Add(Path.Combine(program, vendor));
        roots.Add(Path.Combine(user, ".jdks"));
        roots.Add(Path.Combine(user, "scoop", "apps"));
        var selected = new List<string>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length))
            if (!broad.Contains(root) && !SamePath(root, Path.GetPathRoot(root)!) && !selected.Any(parent => root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            { selected.Add(root); if (selected.Count == 64) break; }
        return selected.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private async Task RefreshMonitoringRootsAsync()
    {
        if (monitor is null || stopping) return;
        var roots = MonitoringRoots(committedSettings.Installations.Select(x => x.Path).Concat(Candidates.Select(x => x.Path)));
        if (roots.SequenceEqual(monitoredRoots, StringComparer.OrdinalIgnoreCase)) return;
        try { await Task.Run(() => monitor.UpdateJavaRoots(roots), lifetime.Token); monitoredRoots = roots; }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error) { ReportError(error); }
    }
    private async Task RefreshCurrentAsync()
    {
        var env = await new WindowsEnvironmentStore().ReadAsync(lifetime.Token);
        CurrentJavaHome = env.UserJavaHome.Exists ? env.UserJavaHome.Value ?? "" : env.MachineJavaHome.Value ?? "";
        EffectiveJavaPath = EnvironmentSwitchService.ResolveFirstJava(env, File.Exists);
        CurrentSelectedId = Settings.Installations.FirstOrDefault(x => SamePath(Path.Combine(x.Path, "bin", "java.exe"), EffectiveJavaPath ?? ""))?.Id;
    }
    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string Signature(JavaInstallation java) => java.Path.ToUpperInvariant() + "|" + java.Fingerprint;
    private async Task<uint> NotifyAsync(string title, string body, string? candidateId = null)
    {
        try
        {
            var result = await notifications.ShowAsync(title, body, candidateId, Text("添加", "Add", "新增", "追加"), Text("忽略", "Ignore", "忽略", "無視"));
            if (result.Id == 0)
            {
                LogMessage($"Notification unavailable: {result.Setting}; elevated={NotificationProcess.IsElevated()}; host={notifications.HostProcessId}");
                ShowNotificationWarning(NotificationFailureText(result.Setting));
            }
            else notificationWarningShown = false;
            return result.Id;
        }
        catch (OperationCanceledException) when (stopping) { return 0; }
        catch (Exception error)
        {
            Log(error);
            ShowNotificationWarning(Text("通知服务连接或发送失败，请重新启动 JavaSelect 后重试。", "The notification service could not connect or send. Restart JavaSelect and try again.", "通知服務連線或傳送失敗，請重新啟動 JavaSelect 後重試。", "通知サービスに接続または送信できませんでした。JavaSelect を再起動して再試行してください。"));
            return 0;
        }
    }
    private string NotificationFailureText(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForApplication => Text("JavaSelect 的系统通知已关闭，请在 Windows 通知设置中开启。", "JavaSelect notifications are disabled. Enable them in Windows notification settings.", "JavaSelect 的系統通知已關閉，請在 Windows 通知設定中開啟。", "JavaSelect の通知が無効です。Windows の通知設定で有効にしてください。"),
        AppNotificationSetting.DisabledForUser => Text("Windows 的通知总开关已关闭，请在系统设置中开启。", "Windows notifications are disabled globally. Enable them in system settings.", "Windows 的通知總開關已關閉，請在系統設定中開啟。", "Windows の通知が全体で無効です。システム設定で有効にしてください。"),
        AppNotificationSetting.DisabledByGroupPolicy => Text("Windows 策略禁止发送通知，请联系系统管理员。", "Windows policy blocks notifications. Contact your administrator.", "Windows 原則禁止傳送通知，請聯絡系統管理員。", "Windows ポリシーで通知が禁止されています。管理者に連絡してください。"),
        AppNotificationSetting.Unsupported => Text("当前运行方式不支持系统通知，请重新启动 JavaSelect 以连接通知服务。", "Notifications are unsupported in this process. Restart JavaSelect to reconnect its notification service.", "目前執行方式不支援系統通知，請重新啟動 JavaSelect 以連接通知服務。", "現在の実行方式では通知を使用できません。JavaSelect を再起動して通知サービスに接続してください。"),
        _ => Text("系统通知注册不可用，请重新启动或重新安装 JavaSelect。", "Notification registration is unavailable. Restart or reinstall JavaSelect.", "系統通知註冊無法使用，請重新啟動或重新安裝 JavaSelect。", "通知の登録を利用できません。JavaSelect を再起動または再インストールしてください。")
    };
    private void ShowNotificationWarning(string message)
    {
        if (notificationWarningShown || stopping) return;
        notificationWarningShown = true;
        SetStatus(message);
    }
    public async Task TestNotificationAsync()
    {
        if (IsReadOnly) { SetStatus(Text("预览模式不发送系统通知。", "System notifications are unavailable in preview mode.", "預覽模式不傳送系統通知。", "プレビューモードではシステム通知を送信しません。")); return; }
        notificationWarningShown = false;
        var id = await NotifyAsync("JavaSelect", Text("这是一条测试通知。点击可返回 JavaSelect。", "This is a test notification. Click to return to JavaSelect.", "這是一則測試通知。點擊可返回 JavaSelect。", "テスト通知です。クリックすると JavaSelect に戻ります。"));
        if (id != 0)
        {
            LogMessage($"Test notification accepted by Windows: {id}");
            SetStatus(Text("测试通知已发送。若未看到，请检查 Windows 通知和勿扰模式。", "Test notification sent. If it is not visible, check Windows notifications and Do not disturb.", "測試通知已傳送。若未看到，請檢查 Windows 通知及勿擾模式。", "テスト通知を送信しました。表示されない場合は Windows の通知と応答不可モードを確認してください。"));
        }
    }
    public void OpenConfigurationFolder() => OpenLink(store.DirectoryPath);
    public void OpenEnvironmentSettings() => Process.Start(new ProcessStartInfo("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables") { UseShellExecute = true });
    public static void OpenLink(string link) => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
    public void ReportError(Exception error) { Log(error); SetStatus(error.Message); }
    public void SetStatus(string status) { Status = status; StatusChanged?.Invoke(this, status); }
    public string Text(string zh, string en, string tw, string ja) => Settings.Language switch { "en-US" => en, "zh-TW" => tw, "ja-JP" => ja, _ => zh };
    private void Log(Exception error) => LogMessage(error.ToString());
    private void LogMessage(string message) { try { Directory.CreateDirectory(store.DirectoryPath); var path = Path.Combine(store.DirectoryPath, "app.log"); if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path); File.AppendAllText(path, DateTimeOffset.Now + " " + message + Environment.NewLine); } catch { } }
    public Task ShutdownAsync() => shutdownTask ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        stopping = true; lifetime.Cancel();
        if (monitorStartTask is not null) await monitorStartTask;
        await operations.WaitAsync();
        operations.Release();
        if (monitor is not null) await Task.Run(monitor.Dispose);
        foreach (var task in new[] { environmentTask, retryTask, updatesTask, aboutTask, downloadUpdateTask, requestUpdateInstallTask }.OfType<Task>())
            try { await task; } catch (OperationCanceledException) { } catch (Exception error) { Log(error); }
        await notifications.DisposeAsync();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; stopping = true; lifetime.Cancel();
        monitor?.Dispose(); notifications.Dispose(); community.Dispose(); updater.Dispose();
    }
}
