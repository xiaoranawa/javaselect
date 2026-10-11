using JavaSelect.App.Services;
using JavaSelect.Core;

namespace JavaSelect.App;

public sealed partial class AppController
{
    public Task UpdateInstallationAsync(string id, string path, string alias, bool showInLauncher) => RunOperationAsync(async () =>
    {
        var draft = CloneSettings(committedSettings);
        var installation = draft.Installations.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException(Text("此版本已被移除，请刷新列表后重试。", "This version was removed. Refresh the list and try again.", "此版本已被移除，請重新整理清單後再試。", "このバージョンは削除されました。一覧を更新して再試行してください。"));
        var normalized = JavaDiscoveryService.NormalizeJavaHome(path);
        if (normalized is null) throw new IOException(Text("请输入包含 bin\\java.exe 的有效 Java 安装目录。", "Enter a valid Java folder containing bin\\java.exe.", "請輸入包含 bin\\java.exe 的有效 Java 安裝目錄。", "bin\\java.exe を含む有効な Java フォルダーを入力してください。"));
        var previousPath = JavaDiscoveryService.NormalizeJavaHome(installation.Path) ?? installation.Path;
        var pathChanged = !SamePath(previousPath, normalized);
        if (pathChanged && id == committedSettings.LastSelectedId)
            throw new InvalidOperationException(Text("请先切换到其他版本，再修改当前版本的安装目录。", "Switch to another version before changing the current version's installation folder.", "請先切換到其他版本，再修改目前版本的安裝目錄。", "別のバージョンに切り替えてから、現在のバージョンのインストール先を変更してください。"));
        EnsureUniquePath(normalized);
        if (pathChanged)
        {
            var verified = await discovery.ProbeAsync(normalized, lifetime.Token);
            if (verified?.IsValid != true)
                throw new IOException(Text("未在该目录检测到可用的 Java，请重新选择安装目录。", "No usable Java was found in this folder. Choose another installation folder.", "未在此目錄偵測到可用的 Java，請重新選擇安裝目錄。", "このフォルダーに有効な Java が見つかりません。インストール先を選び直してください。"));
            EnsureUniquePath(verified.Path);
            verified.IconPath = await Task.Run(() => IconCache.GetJavaIcon(verified, store.DirectoryPath), lifetime.Token);
            CopyInstallation(verified, installation, preserveIdentity: true, preserveEdits: true);
        }
        installation.Alias = alias.Trim();
        installation.ShowInLauncher = showInLauncher;

        await store.SaveAsync(draft, lifetime.Token);
        Commit(draft, preferences: false);
        var live = Settings.Installations.First(x => x.Id == id);
        live.Alias = installation.Alias;
        live.ShowInLauncher = installation.ShowInLauncher;
        Candidates = Candidates.Where(candidate => !committedSettings.Installations.Any(saved => SamePath(saved.Path, candidate.Path))).ToArray();
        if (pathChanged && repairTargetId == id) { repairTargetId = null; ConflictPath = null; }

        // Derived caches do not roll back an already committed installation edit.
        try { await SaveCandidatesAsync(); }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log(error); }
        if (!stopping)
        {
            try { await RefreshCurrentAsync(); }
            catch (OperationCanceledException) when (stopping) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { ReportError(error); }
            await RefreshMonitoringRootsAsync();
        }

        void EnsureUniquePath(string candidate)
        {
            if (draft.Installations.Any(x => x.Id != id && SamePath(JavaDiscoveryService.NormalizeJavaHome(x.Path) ?? x.Path, candidate)))
                throw new InvalidOperationException(Text("该安装目录已在版本列表中，请选择其他目录。", "This installation folder is already in the version list. Choose another folder.", "此安裝目錄已在版本清單中，請選擇其他目錄。", "このインストール先は既に一覧にあります。別のフォルダーを選択してください。"));
        }
    });
}
