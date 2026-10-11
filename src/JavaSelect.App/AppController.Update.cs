using JavaSelect.App.Localization;
using JavaSelect.App.Services;

namespace JavaSelect.App;

public sealed partial class AppController
{
    private readonly UpdateService updater;
    private readonly InstalledApplication? installedApplication;
    private ReleaseInfo? availableRelease;
    private DownloadedInstaller? downloadedInstaller;
    private Task<string?>? downloadUpdateTask;
    private Task? requestUpdateInstallTask;
    private int updateDownloadGeneration;

    public bool IsInstalled => installedApplication is not null;
    public bool HasUpdate => availableRelease?.IsNew == true;
    public bool CanDownloadUpdate => !IsReadOnly && IsInstalled && HasUpdate && availableRelease?.Installer is not null;
    public bool IsUpdateReadyToInstall => downloadedInstaller is not null && availableRelease is not null
        && downloadedInstaller.Version == ReleaseValidation.NormalizeVersion(availableRelease.Version);
    public bool IsDownloadingUpdate { get; private set; }
    public double? UpdateDownloadProgress { get; private set; }
    public event EventHandler? UpdateProgressChanged;
    public event EventHandler<string>? UpdateInstallRequested;

    public Task<string?> DownloadUpdateAsync()
    {
        if (stopping) return Task.FromResult<string?>(null);
        if (downloadUpdateTask is { IsCompleted: false }) return downloadUpdateTask;
        if (!CanDownloadUpdate || IsCheckingUpdates) return Task.FromResult<string?>(null);
        return downloadUpdateTask = DownloadUpdateCoreAsync(availableRelease!);
    }

    private async Task<string?> DownloadUpdateCoreAsync(ReleaseInfo release)
    {
        var generation = ++updateDownloadGeneration;
        IsDownloadingUpdate = true; UpdateDownloadProgress = null;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (downloadedInstaller?.Version == ReleaseValidation.NormalizeVersion(release.Version) && await updater.VerifyInstallerAsync(downloadedInstaller, lifetime.Token))
                return downloadedInstaller.Path;
            downloadedInstaller = null;
            var progress = new Progress<double>(value => dispatcher.TryEnqueue(() =>
            {
                if (stopping || !IsDownloadingUpdate || generation != updateDownloadGeneration || !double.IsFinite(value)) return;
                var percent = Math.Clamp(Math.Floor(value), 0, 100);
                if (UpdateDownloadProgress == percent) return;
                UpdateDownloadProgress = percent;
                UpdateProgressChanged?.Invoke(this, EventArgs.Empty);
            }));
            downloadedInstaller = await updater.DownloadInstallerAsync(release, progress, lifetime.Token);
            UpdateDownloadProgress = 100;
            UpdateStatus = UpdateText("updateReady");
            return downloadedInstaller.Path;
        }
        catch (OperationCanceledException) when (stopping) { return null; }
        catch (Exception error)
        {
            Log(error); downloadedInstaller = null;
            UpdateStatus = UpdateText("updateDownloadFailed");
            SetStatus(UpdateStatus); return null;
        }
        finally
        {
            IsDownloadingUpdate = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task RequestUpdateInstallAsync(string installerPath)
    {
        if (requestUpdateInstallTask is { IsCompleted: false }) return requestUpdateInstallTask;
        return requestUpdateInstallTask = RequestUpdateInstallCoreAsync(installerPath);
    }

    internal Task<FileStream?> LockUpdateInstallerAsync(string path)
    {
        var installer = downloadedInstaller;
        if (installer is null || !IsInstalled || !SamePath(installer.Path, path)) return Task.FromResult<FileStream?>(null);
        return updater.OpenVerifiedInstallerAsync(installer);
    }

    private async Task RequestUpdateInstallCoreAsync(string installerPath)
    {
        if (stopping || IsReadOnly || !IsInstalled || IsDownloadingUpdate) return;
        var installer = downloadedInstaller;
        try
        {
            if (installer is null || !CanDownloadUpdate || !IsUpdateReadyToInstall || !Path.IsPathFullyQualified(installerPath)
                || !SamePath(installer.Path, Path.GetFullPath(installerPath))
                || !await updater.VerifyInstallerAsync(installer, lifetime.Token))
            {
                downloadedInstaller = null;
                UpdateStatus = UpdateText("updateVerificationFailed");
                SetStatus(UpdateStatus); Changed?.Invoke(this, EventArgs.Empty); return;
            }
            UpdateInstallRequested?.Invoke(this, installer.Path);
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error)
        {
            Log(error); downloadedInstaller = null;
            UpdateStatus = UpdateText("updateVerificationFailed");
            SetStatus(UpdateStatus); Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string UpdateText(string key) => new Localizer(Settings.Language)[key];
}
