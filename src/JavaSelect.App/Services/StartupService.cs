using Microsoft.Win32;

namespace JavaSelect.App.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JavaSelect";

    public bool IsEnabled(string executablePath)
    {
        var command = BuildCommand(executablePath);
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, command, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        var command = BuildCommand(executablePath);
        if (enabled)
        {
            if (!File.Exists(executablePath)) throw new FileNotFoundException("Startup executable was not found.", executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return;
        }
        using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var saved = existing?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (saved is null) return;
        if (saved is not string text || !string.Equals(text, command, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The JavaSelect startup entry points to a different executable and was left unchanged.");
        existing!.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string BuildCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains('"') || executablePath.Any(char.IsControl))
            throw new ArgumentException("An absolute executable path is required.", nameof(executablePath));
        var command = $"\"{Path.GetFullPath(executablePath)}\" --background";
        if (command.Length > 260) throw new ArgumentException("The startup command exceeds the Windows Run key limit.", nameof(executablePath));
        return command;
    }
}
