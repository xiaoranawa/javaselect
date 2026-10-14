using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace JavaSelect.App.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JavaSelect";

    private const string TaskAuthor = "JavaSelect.Startup.v1";
    private const string TaskArguments = "--background";
    private const string ForeignTaskMessage = "The JavaSelect startup task was changed by another program and was left untouched.";
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>How the per-user startup task relates to one particular JavaSelect.exe.</summary>
    internal enum TaskKind { Missing, Healthy, Disabled, OtherInstall, Foreign }

    public bool IsEnabled(string executablePath)
    {
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        using var scheduler = new SchedulerConnection();
        return Classify(scheduler.Read(TaskName(sid)), path, sid) == TaskKind.Healthy;
    }

    /// <summary>
    /// Aligns sign-in startup with the saved preference at launch (the app is already elevated here).
    /// A leftover HKCU Run value starts an unelevated copy that must raise UAC at every sign-in, so it is always retired.
    /// </summary>
    public void ReconcileAtLaunch(bool enabled, string executablePath)
    {
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        if (enabled)
        {
            using var scheduler = new SchedulerConnection();
            var name = TaskName(sid); var snapshot = scheduler.Read(name);
            var kind = Classify(snapshot, path, sid);
            if (kind == TaskKind.Foreign) throw new InvalidOperationException(ForeignTaskMessage);
            // A live task of another installation stays with that installation; only a missing, disabled or dead one is repaired.
            if (kind is TaskKind.Missing or TaskKind.Disabled || kind == TaskKind.OtherInstall && !OtherInstallExists(snapshot!.Value.Xml, sid))
                Register(scheduler, name, path, sid);
        }
        RemoveLegacyEntry(path);
    }

    /// <param name="takeOver">Replace a startup task that points at another JavaSelect.exe (explicit user or Setup choice only).</param>
    public void SetEnabled(bool enabled, string executablePath, bool takeOver = true)
    {
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException("Administrator permission is required to change JavaSelect startup.");
        using var scheduler = new SchedulerConnection();
        var name = TaskName(sid); var snapshot = scheduler.Read(name);
        var kind = Classify(snapshot, path, sid);
        if (enabled)
        {
            if (kind == TaskKind.Foreign) throw new InvalidOperationException(ForeignTaskMessage);
            if (!File.Exists(path)) throw new FileNotFoundException("Startup executable was not found.", path);
            // Saving unrelated settings must not take startup away from another installation.
            if (kind == TaskKind.OtherInstall && !takeOver && OtherInstallExists(snapshot!.Value.Xml, sid)) return;
            Register(scheduler, name, path, sid);
        }
        else if (kind is TaskKind.Healthy or TaskKind.Disabled) scheduler.Delete(name);
        RemoveLegacyEntry(path);
    }

    public void RemoveOwnedStartup(string executablePath)
    {
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException("Administrator permission is required to remove JavaSelect startup.");
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        using var scheduler = new SchedulerConnection();
        var name = TaskName(sid);
        if (Classify(scheduler.Read(name), path, sid) is TaskKind.Healthy or TaskKind.Disabled) scheduler.Delete(name);
        RemoveLegacyEntry(path);
    }

    private static void Register(SchedulerConnection scheduler, string name, string path, string sid)
    {
        scheduler.Register(name, BuildTaskXml(path, sid), sid);
        if (Classify(scheduler.Read(name), path, sid) != TaskKind.Healthy) throw new IOException("The JavaSelect startup task could not be verified.");
    }

    private static TaskKind Classify((string Xml, bool Enabled)? task, string path, string sid) =>
        task is { } value ? Classify(value.Xml, value.Enabled, path, sid) : TaskKind.Missing;

    internal static TaskKind Classify(string xml, bool taskEnabled, string path, string sid)
    {
        if (IsOwnedTask(xml, path, sid))
            return taskEnabled && IsOwnedTask(xml, path, sid, requireEnabledTrigger: true) ? TaskKind.Healthy : TaskKind.Disabled;
        return IsJavaSelectTask(xml, sid) ? TaskKind.OtherInstall : TaskKind.Foreign;
    }

    private static bool OtherInstallExists(string xml, string sid) => ReadTaskCommand(xml, sid, requireEnabledTrigger: false) is { } command && File.Exists(command);

    private static void RemoveLegacyEntry(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (string.Equals(key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            LegacyCommand(path), StringComparison.OrdinalIgnoreCase)) key!.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string TaskName(string sid) => "JavaSelect-" + ValidateSid(sid);
    internal static string LegacyCommand(string path) => $"\"{ValidatePath(path)}\" {TaskArguments}";

    internal static string BuildTaskXml(string executablePath, string sid)
    {
        var path = ValidatePath(executablePath); ValidateSid(sid);
        XElement Element(string name, object? content) => new(Namespace + name, content);
        var document = new XDocument(new XElement(Namespace + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Author", TaskAuthor)),
            Element("Triggers", new XElement(Namespace + "LogonTrigger", Element("Enabled", true), Element("UserId", sid))),
            Element("Principals", new XElement(Namespace + "Principal", new XAttribute("id", "CurrentUser"),
                Element("UserId", sid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            new XElement(Namespace + "Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", false), Element("StopIfGoingOnBatteries", false),
                Element("AllowHardTerminate", false), Element("StartWhenAvailable", true),
                Element("AllowStartOnDemand", true), Element("Enabled", true), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(Namespace + "Actions", new XAttribute("Context", "CurrentUser"),
                new XElement(Namespace + "Exec", Element("Command", path), Element("Arguments", TaskArguments),
                    Element("WorkingDirectory", Path.GetDirectoryName(path))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The task was written by JavaSelect for this account and launches exactly this executable.</summary>
    internal static bool IsOwnedTask(string xml, string executablePath, string sid, bool requireEnabledTrigger = false)
    {
        try { return string.Equals(ReadTaskCommand(xml, sid, requireEnabledTrigger), ValidatePath(executablePath), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The task was written by JavaSelect for this account, but may point at another JavaSelect.exe.</summary>
    internal static bool IsJavaSelectTask(string xml, string sid) =>
        ReadTaskCommand(xml, sid, requireEnabledTrigger: false) is { } command
        && string.Equals(Path.GetFileName(command), "JavaSelect.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the executable of a structurally exact JavaSelect startup task for <paramref name="sid"/>, otherwise null.</summary>
    private static string? ReadTaskCommand(string xml, string sid, bool requireEnabledTrigger)
    {
        try
        {
            ValidateSid(sid);
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 131072 });
            var task = XDocument.Load(reader).Root;
            if (task?.Name != Namespace + "Task" || (string?)task.Element(Namespace + "RegistrationInfo")?.Element(Namespace + "Author") != TaskAuthor) return null;
            var principals = task.Element(Namespace + "Principals")?.Elements().ToArray();
            if (principals?.Length != 1) return null;
            var principal = principals[0];
            if (!SameAccount((string?)principal.Element(Namespace + "UserId"), sid) || principal.Element(Namespace + "GroupId") is not null
                || (string?)principal.Element(Namespace + "LogonType") != "InteractiveToken"
                || (string?)principal.Element(Namespace + "RunLevel") != "HighestAvailable") return null;
            var triggers = task.Element(Namespace + "Triggers")?.Elements().ToArray();
            // Task Scheduler stores the trigger account as DOMAIN\name even when it was registered with a SID.
            if (triggers?.Length != 1 || triggers[0].Name != Namespace + "LogonTrigger"
                || !SameAccount((string?)triggers[0].Element(Namespace + "UserId"), sid)) return null;
            if (requireEnabledTrigger && (bool?)triggers[0].Element(Namespace + "Enabled") == false) return null;
            var actions = task.Element(Namespace + "Actions")?.Elements().ToArray();
            if (actions?.Length != 1 || actions[0].Name != Namespace + "Exec") return null;
            var command = (string?)actions[0].Element(Namespace + "Command");
            if (string.IsNullOrEmpty(command) || (string?)actions[0].Element(Namespace + "Arguments") != TaskArguments
                || !string.Equals((string?)actions[0].Element(Namespace + "WorkingDirectory"), Path.GetDirectoryName(command), StringComparison.OrdinalIgnoreCase)) return null;
            return command;
        }
        catch (Exception error) when (error is XmlException or ArgumentException or FormatException) { return null; }
    }

    /// <summary>True when <paramref name="account"/> (a SID or DOMAIN\name) identifies the account with <paramref name="sid"/>.</summary>
    internal static bool SameAccount(string? account, string sid)
    {
        if (string.IsNullOrWhiteSpace(account)) return false;
        account = account.Trim();
        if (string.Equals(account, sid, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User?.Value == sid && string.Equals(account, identity.Name, StringComparison.OrdinalIgnoreCase)) return true;
            var resolved = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(account) : (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            return string.Equals(resolved.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (SystemException) { return false; }
    }

    private static string ValidateSid(string sid)
    {
        if (new SecurityIdentifier(sid).Value != sid) throw new ArgumentException("A canonical Windows user SID is required.", nameof(sid));
        return sid;
    }

    private static string ValidatePath(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains('"') || executablePath.Any(char.IsControl))
            throw new ArgumentException("An absolute executable path is required.", nameof(executablePath));
        return Path.GetFullPath(executablePath);
    }

    private sealed class SchedulerConnection : IDisposable
    {
        private readonly List<object> objects = [];
        private readonly dynamic folder;
        internal SchedulerConnection()
        {
            try
            {
                dynamic service = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!);
                service.Connect(); folder = Track(service.GetFolder(@"\"));
            }
            catch { Dispose(); throw; }
        }
        internal (string Xml, bool Enabled)? Read(string name)
        {
            try
            {
                dynamic task = Track(folder.GetTask(name));
                return ((string)task.Xml, (bool)task.Enabled);
            }
            catch (Exception error) when (error is COMException or FileNotFoundException && error.HResult == unchecked((int)0x80070002)) { return null; }
        }
        internal void Register(string name, string xml, string sid) => Track(folder.RegisterTask(name, xml, 6, sid, null, 3, null));
        internal void Delete(string name) => folder.DeleteTask(name, 0);
        private object Track(object value) { objects.Add(value); return value; }
        public void Dispose()
        {
            for (var index = objects.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(objects[index])) Marshal.ReleaseComObject(objects[index]);
            objects.Clear();
        }
    }
}
