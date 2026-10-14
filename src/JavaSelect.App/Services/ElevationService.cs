using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace JavaSelect.App.Services;

internal static class ElevationService
{
    internal const string UserArgument = "--elevated-user-sid";

    internal static void RestoreInstallerTemporaryDirectory()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var name in new[] { "TEMP", "TMP" })
        {
            if (!IsInstallerTemporaryDirectory(Environment.GetEnvironmentVariable(name), windows)) continue;
            var saved = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            var replacement = string.IsNullOrWhiteSpace(saved) ? "" : Environment.ExpandEnvironmentVariables(saved);
            if (!Path.IsPathFullyQualified(replacement) || IsInstallerTemporaryDirectory(replacement, windows))
                replacement = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            Environment.SetEnvironmentVariable(name, replacement, EnvironmentVariableTarget.Process);
        }
    }

    internal static bool IsInstallerTemporaryDirectory(string? path, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(Path.GetDirectoryName(full), Path.Combine(windowsDirectory, "Temp"), StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(Path.GetFileName(full), @"^JavaSelect-Setup-(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new UnauthorizedAccessException("The Windows account could not be identified.");
    }

    internal static async Task<bool> EnsureElevatedAsync(string[] command)
    {
        if (BypassesElevation(command)) return true;
        var sid = CurrentUserSid();
        ValidateAccount(command, sid);
        if (IsElevated()) return true;
        if (command.Contains("--no-elevation")) throw new UnauthorizedAccessException("Administrator permission is required. Run this maintenance operation interactively or from an administrator process.");
        if (command.Contains(UserArgument)) throw new UnauthorizedAccessException("JavaSelect requires administrator permission for the current Windows account.");
        var executable = Environment.ProcessPath ?? throw new IOException("The application path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(UserArgument); start.ArgumentList.Add(sid);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Administrator startup failed.");
            if (WaitsForElevatedChild(command))
            {
                await process.WaitForExitAsync();
                Environment.ExitCode = process.ExitCode;
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { Environment.ExitCode = 1223; }
        return false;
    }

    internal static bool BypassesElevation(IEnumerable<string> arguments) => arguments.Any(value =>
        value is "--smoke" or "--notification-host" || value.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal));

    /// <summary>Maintenance commands run by Setup: the unelevated stub must wait for the elevated child and return its exit code.</summary>
    internal static bool WaitsForElevatedChild(IEnumerable<string> arguments) => arguments.Any(value =>
        value is "--quit-for-update" or "--remove-startup" or "--enable-startup");

    internal static void ValidateAccount(IReadOnlyList<string> command, string actualSid)
    {
        var indices = Enumerable.Range(0, command.Count).Where(index => command[index] == UserArgument).ToArray();
        if (indices.Length == 0) return;
        if (indices.Length != 1 || indices[0] + 1 >= command.Count || command[indices[0] + 1] != actualSid)
            throw new UnauthorizedAccessException("Use the same Windows account to run JavaSelect as administrator. No settings or environment variables were changed.");
    }
}
