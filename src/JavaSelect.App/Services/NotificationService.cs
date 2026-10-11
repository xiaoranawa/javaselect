using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace JavaSelect.App.Services;

public sealed record NotificationAction(string Action, string? CandidateId);
internal sealed record NotificationDelivery(uint Id, AppNotificationSetting Setting);
internal sealed class NotificationService : IDisposable, IAsyncDisposable
{
    private AppNotificationManager? manager;
    private NotificationBroker? broker;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool launchHandled, notificationLaunch;
    public event EventHandler<NotificationAction>? ActionInvoked;
    public async Task InitializeAsync()
    {
        await initialization.WaitAsync(lifetime.Token);
        try
        {
            if (manager is not null || broker?.IsConnected == true) return;
            if (NotificationProcess.IsElevated())
            {
                if (broker is not null) await broker.DisposeAsync();
                broker = await NotificationBroker.ConnectAsync(action => ActionInvoked?.Invoke(this, action), lifetime.Token);
                return;
            }
            var instance = AppNotificationManager.Default; instance.NotificationInvoked += Invoked;
            try
            {
                instance.Register("JavaSelect", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png")));
                NotificationShortcut.EnsureRegistered(Environment.ProcessPath!);
                manager = instance;
            }
            catch
            {
                instance.NotificationInvoked -= Invoked;
                try { instance.Unregister(); } catch { }
                throw;
            }
        }
        finally { initialization.Release(); }
    }
    public bool HandleLaunchActivation()
    {
        if (broker is not null) return false;
        if (manager is null) throw new InvalidOperationException("Windows notification registration is unavailable.");
        if (launchHandled) return notificationLaunch;
        // Cold-start arguments are delivered through AppLifecycle, not NotificationInvoked.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        launchHandled = true;
        notificationLaunch = activation.Kind == ExtendedActivationKind.AppNotification;
        if (notificationLaunch && activation.Data is AppNotificationActivatedEventArgs args) HandleActivation(args);
        return notificationLaunch;
    }
    public async Task<NotificationDelivery> ShowAsync(string title, string body, string? candidateId = null, string add = "Add", string ignore = "Ignore")
    {
        await InitializeAsync();
        if (broker is not null) return await broker.ShowAsync(title, body, candidateId, add, ignore, lifetime.Token);
        if (manager is null) throw new InvalidOperationException("Windows notification registration is unavailable.");
        var setting = manager.Setting;
        if (setting != AppNotificationSetting.Enabled) return new(0, setting);
        var builder = new AppNotificationBuilder().AddText(title).AddText(body).AddArgument("action", "open");
        if (candidateId is not null)
            builder.AddButton(new AppNotificationButton(add).AddArgument("action", "add").AddArgument("id", candidateId))
                .AddButton(new AppNotificationButton(ignore).AddArgument("action", "ignore").AddArgument("id", candidateId));
        var toast = builder.BuildNotification(); toast.Expiration = DateTimeOffset.Now.AddDays(1); manager.Show(toast);
        if (toast.Id == 0) throw new InvalidOperationException("Windows did not accept the notification.");
        return new(toast.Id, setting);
    }
    internal AppNotificationSetting Setting => manager?.Setting ?? throw new InvalidOperationException("Windows notification registration is unavailable.");
    internal int? HostProcessId => broker?.ProcessId;
    internal async Task<AppNotificationSetting> GetSettingAsync()
    {
        await InitializeAsync();
        return broker is not null ? await broker.ProbeAsync(lifetime.Token) : Setting;
    }
    internal async Task ClearRegistrationAsync()
    {
        await InitializeAsync();
        if (broker is not null) await broker.ClearRegistrationAsync(lifetime.Token);
        else
        {
            NotificationShortcut.RemoveOwned(Environment.ProcessPath!);
            manager!.UnregisterAll();
            manager.NotificationInvoked -= Invoked;
            manager = null;
        }
    }
    internal async Task<bool> ContainsAsync(uint id)
    {
        await InitializeAsync();
        return broker is not null ? await broker.ContainsAsync(id, lifetime.Token)
            : (await manager!.GetAllAsync()).Any(notification => notification.Id == id);
    }
    internal async Task<NotificationIdentity> GetIdentityAsync()
    {
        await InitializeAsync();
        return broker is not null ? await broker.GetIdentityAsync(lifetime.Token) : NotificationProcess.DescribeIdentity();
    }
    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => HandleActivation(args);
    private void HandleActivation(AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action)) return;
        args.Arguments.TryGetValue("id", out var id);
        var request = new NotificationAction(action, id);
        if (IsValidAction(request)) ActionInvoked?.Invoke(this, request);
    }
    internal static bool IsValidAction(NotificationAction action) => action.Action is "open" or "add" or "ignore"
        && (action.Action == "open" || action.CandidateId is { Length: > 0 and <= 128 } id && id.All(char.IsAsciiLetterOrDigit));
    public void Dispose()
    {
        lifetime.Cancel(); broker?.Dispose(); broker = null;
        Unregister();
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await initialization.WaitAsync();
        try
        {
            if (broker is not null) await broker.DisposeAsync();
            broker = null; Unregister();
        }
        finally { initialization.Release(); }
    }
    private void Unregister()
    {
        if (manager is null) return;
        manager.NotificationInvoked -= Invoked;
        try { manager.Unregister(); } catch (System.Runtime.InteropServices.COMException) { }
        manager = null;
    }
}
