using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using JavaSelect.Core;

namespace JavaSelect.App;

public sealed partial class AppController
{
    public Task<PathRepairPlan?> PreparePathRepairAsync() => RunOperationAsync<PathRepairPlan?>(async () =>
    {
        if (repairTargetId is null) return null;
            var saved = committedSettings.Installations.FirstOrDefault(x => x.Id == repairTargetId);
            if (saved is null) return null;
            var verified = await discovery.ProbeAsync(saved.Path, lifetime.Token);
            if (verified?.IsValid != true) throw new IOException(Text("所选 Java 已不可用，请重新扫描。", "The selected Java is unavailable. Scan again.", "所選 Java 已無法使用，請重新掃描。", "選択した Java が利用できません。再検索してください。"));
            verified.Id = saved.Id;
            return PathRepairPlanner.Create(await new WindowsEnvironmentStore().ReadAsync(lifetime.Token), verified, committedSettings.ManagedPathEntry, File.Exists);
    });

    public async Task RepairPathAsync(PathRepairPlan plan)
    {
        if (IsReadOnly) { SetStatus(Text("只读测试不会修改系统 PATH。", "Read-only testing does not modify the system PATH.", "唯讀測試不會修改系統 PATH。", "読み取り専用テストではシステム PATH を変更しません。")); return; }
        await RunOperationAsync(async () =>
        {
            if (!repairAttempts.CanAttempt(plan, recovery: false)) throw new IOException(RepairStaleText());
            if (!await RepairPathCoreAsync(plan, lifetime.Token, () => repairAttempts.RecordAttempt(plan))) { SetStatus(RepairCancelledText()); return; }
            ConflictPath = null;
            await ApplySelectionCoreAsync(plan.TargetId, false, plan.ExpectedFingerprint, CancellationToken.None, allowAutomaticRepair: false);
        });
    }

    private async Task<(EnvironmentSwitchResult Result, string? Message)> TryRepairAndRetrySelectionAsync(
        JavaInstallation installation, AppSettings draft, EnvironmentSwitchResult failure, bool recovery, CancellationToken token)
    {
        if (IsReadOnly || stopping || failure.Installation is not { IsValid: true } verified) return (failure, null);
        try
        {
            var snapshot = await new WindowsEnvironmentStore().ReadAsync(token);
            var plan = PathRepairPlanner.Create(snapshot, verified, draft.ManagedPathEntry, File.Exists);
            var attempt = await PathRepairWorkflow.RunAsync(plan, failure, repairAttempts, recovery, IsReadOnly,
                async (repairPlan, cancellationToken, elevationStarting) =>
                {
                    SetStatus(Text("检测到 PATH 冲突，正在请求管理员权限…", "PATH conflict detected. Requesting administrator permission…", "偵測到 PATH 衝突，正在要求系統管理員權限…", "PATH の競合を検出しました。管理者権限を要求しています…"));
                    return await RepairPathCoreAsync(repairPlan, cancellationToken, elevationStarting);
                },
                (fingerprint, cancellationToken) => switcher.SwitchAsync(installation, draft, cancellationToken, expectedFingerprint: fingerprint), token);
            return (attempt.Result, attempt.Cancelled ? RepairCancelledText() : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            Log(error);
            return (failure, error.Message);
        }
    }

    private async Task<bool> RepairPathCoreAsync(PathRepairPlan plan, CancellationToken token, Action elevationStarting)
    {
        if (IsReadOnly) return false;
        string? request = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var saved = committedSettings.Installations.FirstOrDefault(x => x.Id == plan.TargetId);
            if (saved is null || !SamePath(saved.Path, plan.TargetJavaHome)) throw new IOException(RepairStaleText());
            var verified = await discovery.ProbeAsync(plan.TargetJavaHome, token);
            var validation = PathRepairPlanner.Validate(plan, await new WindowsEnvironmentStore().ReadAsync(token), verified?.Fingerprint ?? "", File.Exists);
            if (verified?.IsValid != true || !validation.Valid) throw new IOException(RepairStaleText());
            var helper = Path.Combine(AppContext.BaseDirectory, "JavaSelect.Repair.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException(Text("修复组件缺失，请重新解压完整程序包。", "Repair component missing. Extract the complete package again.", "修復元件遺失，請重新解壓完整套件。", "修復コンポーネントがありません。パッケージ全体を再展開してください。"));
            var directory = Path.Combine(store.DirectoryPath, "environment-backups"); Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
            request = Path.Combine(directory, "repair-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllBytesAsync(request, bytes, token);
            var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add(request); info.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(bytes)));
            token.ThrowIfCancellationRequested();
            elevationStarting();
            using var process = Process.Start(info) ?? throw new IOException("Cannot start the repair component.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 6) throw new IOException(Text("PATH 修复失败且未能完整回滚。请打开环境变量设置检查；备份位于 ", "PATH repair failed and rollback was incomplete. Check Environment Variables. Backup: ", "PATH 修復失敗且無法完整還原。請檢查環境變數。備份：", "PATH の修復と復元に失敗しました。環境変数を確認してください。バックアップ: ") + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JavaSelect", "EnvironmentBackups"));
            if (process.ExitCode != 0) throw new IOException(Text("修复未完成。环境可能已变化，请重试或打开环境变量设置。", "Repair did not complete. Retry or open Environment Variables.", "修復未完成，請重試或開啟環境變數設定。", "修復できませんでした。再試行するか環境変数を開いてください。"));
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return false;
        }
        finally { if (request is not null) try { File.Delete(request); } catch (IOException error) { Log(error); } catch (UnauthorizedAccessException error) { Log(error); } }
    }

    private string RepairCancelledText() => Text("已取消修复。下次手动切换时可重试。", "Repair cancelled. Switch manually to try again.", "已取消修復。下次手動切換時可再試。", "修復をキャンセルしました。手動で切り替えると再試行できます。");
    private string RepairStaleText() => Text("环境已变化，请重新准备修复。", "The environment changed. Prepare the repair again.", "環境已變更，請重新準備修復。", "環境が変更されました。修復を準備し直してください。");
}
