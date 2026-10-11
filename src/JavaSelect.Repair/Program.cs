using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using JavaSelect.Core;
using Microsoft.Win32;

if (args.Length != 2 || args[1].Length != 64 || !args[1].All(Uri.IsHexDigit)
    || !PathRepairPlanner.IsLocalFileSystemPath(args[0])) return 3;
using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
PathRepairPlan? plan = null;
var written = false;
var environment = new WindowsEnvironmentStore();
try
{
    EnsureNoReparsePoints(args[0]);
    using var input = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
    if (input.Length is < 1 or > 262144) return 3;
    var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes);
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(args[1]))) return 3;
    try { plan = JsonSerializer.Deserialize<PathRepairPlan>(bytes); }
    catch (JsonException) { return 3; }
    if (plan is null) return 3;
    var snapshot = await environment.ReadAsync();
    if (!PathRepairPlanner.ValidateMachineRepair(plan, snapshot, File.Exists).Valid) return 4;
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JavaSelect", "EnvironmentBackups");
    EnsureNoReparsePoints(directory);
    Directory.CreateDirectory(directory);
    EnsureNoReparsePoints(directory);
    var backup = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
    var backupContents = new MachinePathBackup(plan.OriginalMachinePath, plan.ProposedMachinePath, DateTimeOffset.UtcNow);
    using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await JsonSerializer.SerializeAsync(output, backupContents);
        output.Flush(true);
    }
    EnsureNoReparsePoints(backup);
    using var backupInput = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
    var savedBackup = await JsonSerializer.DeserializeAsync<MachinePathBackup>(backupInput);
    if (savedBackup != backupContents) return 5;
    snapshot = await environment.ReadAsync();
    if (!PathRepairPlanner.ValidateMachineRepair(plan, snapshot, File.Exists).Valid) return 4;
    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true);
    if (key is null) return 5;
    written = true;
    key.SetValue("Path", plan.ProposedMachinePath.Value ?? "", plan.ProposedMachinePath.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    if ((await environment.ReadAsync()).MachinePath != plan.ProposedMachinePath) throw new IOException("PATH verification failed.");
    await environment.BroadcastAsync();
    return 0;
}
catch
{
    if (written && plan is not null)
    {
        try
        {
            var current = (await environment.ReadAsync()).MachinePath;
            if (current == plan.OriginalMachinePath) return 5;
            if (PathRepairPlanner.CanRollbackMachinePath(plan, current))
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true);
                if (key is null) return 6;
                if (plan.OriginalMachinePath.Exists) key.SetValue("Path", plan.OriginalMachinePath.Value ?? "", plan.OriginalMachinePath.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                else key.DeleteValue("Path", throwOnMissingValue: false);
                if ((await environment.ReadAsync()).MachinePath != plan.OriginalMachinePath) return 6;
                await environment.BroadcastAsync();
            }
            else return 6;
        }
        catch { return 6; }
    }
    return 5;
}

static void EnsureNoReparsePoints(string path)
{
    if (!PathRepairPlanner.IsLocalFileSystemPath(path)) throw new IOException("A local filesystem path is required.");
    var current = Path.GetFullPath(path);
    while (!string.IsNullOrEmpty(current))
    {
        try
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Symbolic links and directory junctions are not allowed.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        current = Path.GetDirectoryName(current);
    }
}

internal sealed record MachinePathBackup(EnvironmentValue Original, EnvironmentValue Proposed, DateTimeOffset SavedAt);
