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
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public bool IsEnabled(string executablePath)
    {
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        using var scheduler = new SchedulerConnection();
        var task = scheduler.Find(TaskName(sid));
        return task is not null && (bool)task.Enabled && IsOwnedTask((string)task.Xml, path, sid, requireEnabledTrigger: true);
    }

    public void MigrateLegacyIfEnabled(bool enabled, string executablePath)
    {
        if (enabled && HasLegacyEntry(executablePath)) SetEnabled(true, executablePath);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException("Administrator permission is required to change JavaSelect startup.");
        using var scheduler = new SchedulerConnection();
        var name = TaskName(sid); var existing = scheduler.Find(name);
        if (existing is not null && !IsOwnedTask((string)existing.Xml, path, sid))
            throw new InvalidOperationException("The JavaSelect startup task belongs to another executable or has been changed and was left untouched.");
        if (enabled)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Startup executable was not found.", path);
            scheduler.Register(name, BuildTaskXml(path, sid), sid);
            var saved = scheduler.Find(name);
            if (saved is null || !(bool)saved.Enabled || !IsOwnedTask((string)saved.Xml, path, sid, requireEnabledTrigger: true))
                throw new IOException("The JavaSelect startup task could not be verified.");
        }
        else if (existing is not null) scheduler.Delete(name);
        RemoveLegacyEntry(path);
    }

    public void RemoveOwnedStartup(string executablePath)
    {
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException("Administrator permission is required to remove JavaSelect startup.");
        var path = ValidatePath(executablePath); var sid = ElevationService.CurrentUserSid();
        using var scheduler = new SchedulerConnection();
        var name = TaskName(sid); var existing = scheduler.Find(name);
        if (existing is not null && IsOwnedTask((string)existing.Xml, path, sid)) scheduler.Delete(name);
        RemoveLegacyEntry(path);
    }

    private static bool HasLegacyEntry(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            LegacyCommand(executablePath), StringComparison.OrdinalIgnoreCase);
    }

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

    internal static bool IsOwnedTask(string xml, string executablePath, string sid, bool requireEnabledTrigger = false)
    {
        try
        {
            var path = ValidatePath(executablePath); ValidateSid(sid);
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 131072 });
            var task = XDocument.Load(reader).Root;
            if (task?.Name != Namespace + "Task" || (string?)task.Element(Namespace + "RegistrationInfo")?.Element(Namespace + "Author") != TaskAuthor) return false;
            var principals = task.Element(Namespace + "Principals")?.Elements().ToArray();
            if (principals?.Length != 1) return false;
            var principal = principals[0];
            if ((string?)principal.Element(Namespace + "UserId") != sid || principal.Element(Namespace + "GroupId") is not null
                || (string?)principal.Element(Namespace + "LogonType") != "InteractiveToken"
                || (string?)principal.Element(Namespace + "RunLevel") != "HighestAvailable") return false;
            var triggers = task.Element(Namespace + "Triggers")?.Elements().ToArray();
            if (triggers?.Length != 1 || triggers[0].Name != Namespace + "LogonTrigger"
                || (string?)triggers[0].Element(Namespace + "UserId") != sid) return false;
            if (requireEnabledTrigger && (bool?)triggers[0].Element(Namespace + "Enabled") == false) return false;
            var actions = task.Element(Namespace + "Actions")?.Elements().ToArray();
            if (actions?.Length != 1 || actions[0].Name != Namespace + "Exec") return false;
            return string.Equals((string?)actions[0].Element(Namespace + "Command"), path, StringComparison.OrdinalIgnoreCase)
                && (string?)actions[0].Element(Namespace + "Arguments") == TaskArguments
                && string.Equals((string?)actions[0].Element(Namespace + "WorkingDirectory"), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or FormatException) { return false; }
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
        internal dynamic? Find(string name)
        {
            try { return Track(folder.GetTask(name)); }
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
