using System.Runtime.ExceptionServices;
using JavaSelect.App.Services;

internal static class NotificationShortcutTests
{
    internal static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { RunCore(check); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunCore(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "notification-shortcut-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "one", "JavaSelect.exe"); Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "fixture only");
            var other = Path.Combine(root, "two", "JavaSelect.exe"); Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllText(other, "fixture only");
            var identity = new NotificationShortcutIdentity("{711750BB-B3A4-48B0-8BE1-1F6A9A02A080}", new("A171631B-D029-4E43-A467-2F777DA13E4E"));
            var otherIdentity = new NotificationShortcutIdentity("{8D7E0C21-96C2-4137-B1E0-256104E55A82}", new("C7879183-D125-4D23-9848-FCCBAFC86439"));
            var programs = Path.Combine(root, "programs");
            var created = NotificationShortcut.Ensure(executable, programs, identity);
            var info = NotificationShortcut.Read(created);
            check(info.Target == executable && info.Arguments == "" && info.AppId == identity.AppId && info.Activator == identity.Activator,
                "Native shortcut COM roundtrip preserves target and notification identity");
            check(info.Description == NotificationShortcut.OwnedDescription && Path.GetFileName(created) == "JavaSelect.lnk", "New notification shortcut has explicit ownership and canonical name");
            var saved = File.ReadAllBytes(created); var untouchedTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc); File.SetLastWriteTimeUtc(created, untouchedTime);
            NotificationShortcut.Ensure(executable, programs, identity);
            check(File.GetLastWriteTimeUtc(created) == untouchedTime && File.ReadAllBytes(created).SequenceEqual(saved), "Matching shortcut fast path does not rewrite its file");

            var fallback = NotificationShortcut.Ensure(other, programs, otherIdentity);
            check(fallback != created && File.ReadAllBytes(created).SequenceEqual(saved), "A second executable receives a separate shortcut without overwriting the first");
            NotificationShortcut.RemoveOwned(executable, programs, identity);
            check(NotificationShortcut.Ensure(other, programs, otherIdentity) == fallback && !File.Exists(created),
                "An existing copy-specific shortcut is reused after the canonical owner is removed");
            created = NotificationShortcut.Ensure(executable, programs, identity);
            NotificationShortcut.RemoveOwned(other, programs, otherIdentity);
            check(!File.Exists(fallback) && File.Exists(created), "Cleanup removes only the selected executable's owned shortcut");
            NotificationShortcut.RemoveOwned(executable, programs, otherIdentity);
            check(File.Exists(created), "Cleanup preserves a shortcut with a different notification identity");
            NotificationShortcut.RemoveOwned(executable, programs, identity);
            check(!File.Exists(created), "Cleanup removes a matching owned canonical shortcut");

            NotificationShortcut.SaveNew(created, executable, "", "Installed JavaSelect");
            NotificationShortcut.Ensure(executable, programs, identity);
            info = NotificationShortcut.Read(created);
            check(info.Description == "Installed JavaSelect" && info.AppId == identity.AppId && info.Activator == identity.Activator,
                "Installer shortcut receives notification properties while retaining its description");
            NotificationShortcut.RemoveOwned(executable, programs, identity);
            check(File.Exists(created), "Cleanup preserves installer-owned shortcuts");

            File.Delete(created); NotificationShortcut.SaveNew(created, executable, "--background", "User shortcut");
            var userBytes = File.ReadAllBytes(created);
            fallback = NotificationShortcut.Ensure(executable, programs, identity);
            check(fallback != created && File.ReadAllBytes(created).SequenceEqual(userBytes), "A shortcut with arguments is preserved and receives a separate notification link");
            NotificationShortcut.RemoveOwned(executable, programs, identity);
            check(File.Exists(created) && !File.Exists(fallback), "Cleanup preserves user arguments and removes only its own fallback");
            check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Shortcut writes leave no staging files");

            check(NotificationShortcut.MatchesServer('"' + executable + "\" ----AppNotificationActivated:", executable), "COM server registration accepts the exact executable and SDK activation argument");
            check(!NotificationShortcut.MatchesServer('"' + other + "\" ----AppNotificationActivated:", executable)
                && !NotificationShortcut.MatchesServer('"' + executable + "\" ----AppNotificationActivated: --extra", executable),
                "COM server registration rejects other executables and extra arguments");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
                || !Path.GetFileName(root).StartsWith("notification-shortcut-test-", StringComparison.Ordinal))
                throw new IOException("Refusing to clean an unexpected shortcut fixture directory.");
            Directory.Delete(root, recursive: true);
        }
    }
}
