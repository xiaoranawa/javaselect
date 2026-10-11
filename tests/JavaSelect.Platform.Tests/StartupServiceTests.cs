using System.Xml.Linq;
using System.Runtime.InteropServices;
using JavaSelect.App.Services;

internal static class StartupServiceTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string sid = "S-1-5-21-100-200-300-1001";
        const string path = @"C:\Apps & Tools\JavaSelect.exe";
        var xml = StartupService.BuildTaskXml(path, sid);
        var ns = (XNamespace)"http://schemas.microsoft.com/windows/2004/02/mit/task";
        check(StartupService.IsOwnedTask(xml, path, sid), "Elevated startup XML preserves an escaped executable path and same-user ownership");
        check(!StartupService.IsOwnedTask(xml, @"C:\Other\JavaSelect.exe", sid), "Startup refuses a task belonging to another installation");
        check(!StartupService.IsOwnedTask(xml, path, "S-1-5-21-100-200-300-1002"), "Startup refuses another user's task");
        var document = XDocument.Parse(xml);
        var principal = document.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!;
        check((string?)principal.Element(ns + "LogonType") == "InteractiveToken" && (string?)principal.Element(ns + "RunLevel") == "HighestAvailable",
            "Startup uses only the signed-in account's interactive elevated token without storing credentials");
        check(document.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value == sid,
            "Startup trigger is scoped to the current Windows account");
        check(!StartupService.IsOwnedTask(xml.Replace("HighestAvailable", "LeastPrivilege"), path, sid), "Startup rejects modified privilege settings");
        check(!StartupService.IsOwnedTask(xml.Replace("--background", "--background --other"), path, sid), "Startup rejects modified arguments");
        check(!StartupService.IsOwnedTask(xml.Replace("JavaSelect.Startup.v1", "Other app"), path, sid), "Startup preserves an unrelated task using the same name");
        var disabledTrigger = XDocument.Parse(xml);
        disabledTrigger.Descendants(ns + "LogonTrigger").Single().Element(ns + "Enabled")!.Value = "false";
        check(StartupService.IsOwnedTask(disabledTrigger.ToString(), path, sid)
            && !StartupService.IsOwnedTask(disabledTrigger.ToString(), path, sid, requireEnabledTrigger: true),
            "A disabled login trigger is reported as off and can be explicitly re-enabled by its owner");
        document.Root.Element(ns + "Actions")!.Add(new XElement(ns + "Exec", new XElement(ns + "Command", @"C:\Other.exe")));
        check(!StartupService.IsOwnedTask(document.ToString(), path, sid), "Startup rejects tasks with extra executable actions");
        check(!StartupService.IsOwnedTask("<!DOCTYPE Task [<!ENTITY a 'x'>]><Task>&a;</Task>", path, sid), "Startup XML validation refuses DTD input");
        check(StartupService.LegacyCommand(path) == "\"C:\\Apps & Tools\\JavaSelect.exe\" --background", "Legacy startup matching retains the exact quoted executable path");
        check(ElevationService.BypassesElevation(["JavaSelect.exe", "--smoke"]), "Isolated smoke mode does not prompt for elevation");
        check(ElevationService.BypassesElevation(["JavaSelect.exe", "--notification-host"]), "Notification host stays at ordinary privileges");
        check(ElevationService.BypassesElevation(["JavaSelect.exe", "----AppNotificationActivated:"]), "Cold notification activation stays at ordinary privileges");
        check(!ElevationService.BypassesElevation(["JavaSelect.exe", "--background"]), "Normal automatic startup requires an administrator token");
        ElevationService.ValidateAccount(["JavaSelect.exe", ElevationService.UserArgument, sid], sid);
        check(true, "Elevation accepts the originating Windows account");
        var mismatchRejected = false;
        try { ElevationService.ValidateAccount(["JavaSelect.exe", ElevationService.UserArgument, sid], "S-1-5-21-100-200-300-1002"); }
        catch (UnauthorizedAccessException) { mismatchRejected = true; }
        check(mismatchRejected, "Elevation rejects different-account credentials before reading or writing user settings");
        var duplicateRejected = false;
        try { ElevationService.ValidateAccount([ElevationService.UserArgument, sid, ElevationService.UserArgument, sid], sid); }
        catch (UnauthorizedAccessException) { duplicateRejected = true; }
        check(duplicateRejected, "Elevation rejects ambiguous account markers");
        check(ElevationService.IsInstallerTemporaryDirectory(@"C:\Windows\Temp\JavaSelect-Setup-11223344556677889900aabbccddeeff", @"C:\Windows"),
            "Post-install startup recognizes only the owned bootstrap temporary directory");
        check(!ElevationService.IsInstallerTemporaryDirectory(@"C:\Users\User\Temp\JavaSelect-Setup-11223344556677889900aabbccddeeff", @"C:\Windows")
            && !ElevationService.IsInstallerTemporaryDirectory(@"C:\Windows\Temp\OtherSetup-11223344556677889900aabbccddeeff", @"C:\Windows"),
            "Ordinary process temporary directories are preserved");
        var action = NotificationCommand.FromArguments(["JavaSelect.exe", "--notification-action", "add", "0123456789abcdef"]);
        check(action is { Action: "add", CandidateId: "0123456789abcdef" } && NotificationCommand.Decode(NotificationCommand.Encode(action.Value)) == action,
            "Notification button activation round-trips through the single-instance channel");
        check(NotificationCommand.Decode("notification|add|../settings.json") is null && NotificationCommand.Decode("notification|run|cmd") is null
            && NotificationCommand.Decode("notification|ignore|" + new string('a', 129)) is null,
            "Notification forwarding rejects executable actions, paths, and oversized candidate IDs");
        ValidateTaskSchema(check);
    }

    private static void ValidateTaskSchema(Action<bool, string> check)
    {
        var references = new List<object>();
        object Track(object value) { references.Add(value); return value; }
        try
        {
            dynamic service = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!);
            service.Connect();
            dynamic folder = Track(service.GetFolder(@"\"));
            var sid = ElevationService.CurrentUserSid();
            var xml = StartupService.BuildTaskXml(Environment.ProcessPath!, sid);
            var name = "JavaSelect-SchemaValidation-" + Guid.NewGuid().ToString("N");
            // TASK_VALIDATE_ONLY checks the Windows schema without registering anything.
            object? result = folder.RegisterTask(name, xml, 1, sid, null, 3, null);
            if (result is not null) Track(result);
            check(true, "Windows Task Scheduler accepts startup XML with TASK_VALIDATE_ONLY");
            try { Track(folder.GetTask(name)); }
            catch (Exception error) when (error is COMException or FileNotFoundException && error.HResult == unchecked((int)0x80070002))
            {
                check(true, "Schema validation leaves no registered task behind");
                return;
            }
            check(false, "TASK_VALIDATE_ONLY unexpectedly registered a task");
        }
        finally
        {
            for (var index = references.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(references[index])) Marshal.ReleaseComObject(references[index]);
        }
    }
}
