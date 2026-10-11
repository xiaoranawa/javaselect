using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace JavaSelect.App.Services;

public sealed record NotificationAction(string Action, string? CandidateId);
internal sealed class NotificationService : IDisposable
{
    private AppNotificationManager? manager;
    private bool launchHandled, notificationLaunch;
    public event EventHandler<NotificationAction>? ActionInvoked;
    public void Initialize()
    {
        if (manager is not null) return;
        var instance = AppNotificationManager.Default; instance.NotificationInvoked += Invoked;
        try
        {
            instance.Register("JavaSelect", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png")));
            manager = instance;
        }
        catch { instance.NotificationInvoked -= Invoked; throw; }
    }
    public bool HandleLaunchActivation()
    {
        if (manager is null) throw new InvalidOperationException("Windows notification registration is unavailable.");
        if (launchHandled) return notificationLaunch;
        // Cold-start arguments are delivered through AppLifecycle, not NotificationInvoked.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        launchHandled = true;
        notificationLaunch = activation.Kind == ExtendedActivationKind.AppNotification;
        if (notificationLaunch && activation.Data is AppNotificationActivatedEventArgs args) HandleActivation(args);
        return notificationLaunch;
    }
    public uint Show(string title, string body, string? candidateId = null, string add = "Add", string ignore = "Ignore")
    {
        if (manager is null) throw new InvalidOperationException("Windows notification registration is unavailable.");
        if (manager.Setting != AppNotificationSetting.Enabled) return 0;
        var builder = new AppNotificationBuilder().AddText(title).AddText(body).AddArgument("action", "open");
        if (candidateId is not null)
            builder.AddButton(new AppNotificationButton(add).AddArgument("action", "add").AddArgument("id", candidateId))
                .AddButton(new AppNotificationButton(ignore).AddArgument("action", "ignore").AddArgument("id", candidateId));
        var toast = builder.BuildNotification(); toast.Expiration = DateTimeOffset.Now.AddDays(1); manager.Show(toast);
        if (toast.Id == 0) throw new InvalidOperationException("Windows did not accept the notification.");
        return toast.Id;
    }
    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => HandleActivation(args);
    private void HandleActivation(AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action) || action is not ("open" or "add" or "ignore")) return;
        args.Arguments.TryGetValue("id", out var id);
        if (action != "open" && (string.IsNullOrEmpty(id) || id.Length > 128 || !id.All(char.IsAsciiLetterOrDigit))) return;
        ActionInvoked?.Invoke(this, new NotificationAction(action, id));
    }
    public void Dispose() { if (manager is null) return; manager.NotificationInvoked -= Invoked; try { manager.Unregister(); } catch (System.Runtime.InteropServices.COMException) { } manager = null; }
}
