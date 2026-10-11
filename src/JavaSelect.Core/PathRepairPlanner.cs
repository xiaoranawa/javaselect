namespace JavaSelect.Core;

public sealed record PathRepairEntry(int Index, string OriginalEntry, string ResolvedDirectory, string Reason);

public sealed record PathRepairPlan(
    bool CanRepair,
    bool RequiresElevation,
    string? Error,
    string TargetId,
    string TargetJavaHome,
    string ExpectedFingerprint,
    EnvironmentValue OriginalMachinePath,
    EnvironmentValue ProposedMachinePath,
    EnvironmentValue ExpectedMachineJavaHome,
    EnvironmentValue ExpectedUserJavaHome,
    EnvironmentValue ExpectedUserPath,
    string? ManagedUserPathEntry,
    IReadOnlyList<PathRepairEntry> RemovedEntries)
{
    public bool AffectsOtherUsers => CanRepair && RequiresElevation;
}

public sealed record PathRepairValidation(bool Valid, string? Error = null);

public static class PathRepairPlanner
{
    public static PathRepairPlan Create(EnvironmentSnapshot snapshot, JavaInstallation target, string? managedUserPathEntry, Func<string, bool> fileExists)
    {
        var changes = new List<PathRepairEntry>();
        var proposedPath = snapshot.MachinePath;
        PathRepairPlan Result(bool ready, string? error = null) => new(ready, ready, error, target.Id, target.Path, target.Fingerprint,
            snapshot.MachinePath, proposedPath, snapshot.MachineJavaHome, snapshot.UserJavaHome, snapshot.UserPath, managedUserPathEntry, changes.AsReadOnly());
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.Fingerprint) || !IsLocalFileSystemPath(target.Path))
            return Result(false, "Verify the selected Java installation before preparing a repair.");
        var home = JavaDiscoveryService.NormalizeJavaHome(target.Path);
        if (!EnvironmentSwitchService.EquivalentPath(home, target.Path)) return Result(false, "The selected Java installation path is invalid.");
        var targetBin = Path.Combine(target.Path, "bin");
        var targetExecutable = Path.Combine(targetBin, "java.exe");
        if (!fileExists(targetExecutable)) return Result(false, "The selected Java installation is unavailable.");
        var entries = (snapshot.MachinePath.Value ?? "").Split(';');
        var expanded = EnvironmentSwitchService.ExpandPathEntries(snapshot, true).ToArray();
        for (int index = 0; index < entries.Length; index++)
        {
            var directory = expanded[index];
            if (!Path.IsPathFullyQualified(directory) || directory.Contains('%')) continue;
            if (!IsLocalFileSystemPath(directory)) return Result(false, "Network or device paths require manual environment repair.");
            var executable = Path.Combine(directory, "java.exe");
            if (!fileExists(executable)) continue;
            if (EnvironmentSwitchService.EquivalentPath(executable, targetExecutable)) break;
            string reason;
            if (IsKnownOracleRedirectorDirectory(directory)) reason = "Oracle redirector";
            else if (snapshot.MachinePath.Expandable && IsJavaHomeBinEntry(entries[index])) reason = "System JAVA_HOME entry";
            else return Result(false, "Another Java installation has priority in the system PATH. Edit that entry manually.");
            changes.Add(new(index, entries[index], directory, reason));
        }
        if (changes.Count == 0) return Result(false, "No supported system PATH repair is needed.");
        var removed = changes.Select(x => x.Index).ToHashSet();
        proposedPath = snapshot.MachinePath with { Value = string.Join(';', entries.Where((_, index) => !removed.Contains(index))) };
        var userPath = EnvironmentSwitchService.BuildUserPath(snapshot.UserPath.Value, managedUserPathEntry, targetBin);
        var prospective = snapshot with
        {
            MachinePath = proposedPath,
            UserJavaHome = new(target.Path),
            UserPath = new(userPath, true, snapshot.UserPath.Expandable || userPath.Contains('%'))
        };
        if (!EnvironmentSwitchService.EquivalentPath(EnvironmentSwitchService.ResolveFirstJava(prospective, fileExists), targetExecutable))
            return Result(false, "The selected Java would still not have priority after this repair.");
        return Result(true);
    }

    public static PathRepairValidation Validate(PathRepairPlan plan, EnvironmentSnapshot freshSnapshot, string targetFingerprint, Func<string, bool> fileExists)
    {
        if (plan.ExpectedUserJavaHome != freshSnapshot.UserJavaHome || plan.ExpectedUserPath != freshSnapshot.UserPath)
            return new(false, "The user environment changed after the repair was prepared.");
        if (!string.Equals(plan.ExpectedFingerprint, targetFingerprint, StringComparison.Ordinal))
            return new(false, "The selected Java installation changed after the repair was prepared.");
        return ValidateMachineRepair(plan, freshSnapshot, fileExists);
    }

    public static PathRepairValidation ValidateMachineRepair(PathRepairPlan plan, EnvironmentSnapshot freshSnapshot, Func<string, bool> fileExists)
    {
        if (!plan.CanRepair || !plan.RequiresElevation || plan.RemovedEntries is null || plan.RemovedEntries.Count == 0)
            return new(false, "The repair plan does not contain a supported change.");
        if (plan.OriginalMachinePath != freshSnapshot.MachinePath || plan.ExpectedMachineJavaHome != freshSnapshot.MachineJavaHome)
            return new(false, "The system environment changed after the repair was prepared.");
        var originalUser = freshSnapshot with { UserJavaHome = plan.ExpectedUserJavaHome, UserPath = plan.ExpectedUserPath, UserVariables = null };
        var rebuilt = Create(originalUser, new JavaInstallation { Id = plan.TargetId, Path = plan.TargetJavaHome, Fingerprint = plan.ExpectedFingerprint, IsValid = true }, plan.ManagedUserPathEntry, fileExists);
        if (!rebuilt.CanRepair || rebuilt.ProposedMachinePath != plan.ProposedMachinePath || !rebuilt.RemovedEntries.SequenceEqual(plan.RemovedEntries))
            return new(false, "The repair plan is stale or contains changes outside the supported Java PATH entries.");
        return new(true);
    }

    public static PathRepairValidation ValidateMachineRepair(PathRepairPlan plan, EnvironmentValue freshMachineHome, EnvironmentValue freshMachinePath, Func<string, bool> fileExists)
        => ValidateMachineRepair(plan, new(plan.ExpectedUserJavaHome, plan.ExpectedUserPath, freshMachineHome, freshMachinePath), fileExists);

    public static bool CanRollbackMachinePath(PathRepairPlan plan, EnvironmentValue currentMachinePath)
        => plan.CanRepair && currentMachinePath == plan.ProposedMachinePath;

    public static bool IsLocalFileSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        if (path.AsSpan(2).Contains(':')) return false;
        try { return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).IndexOf('\0') < 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    public static bool IsKnownOracleRedirectorDirectory(string directory)
    {
        foreach (var common in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86) }.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in new[] { "javapath", "java8path" })
                if (EnvironmentSwitchService.EquivalentPath(directory, Path.Combine(common, "Oracle", "Java", name))) return true;
        return false;
    }

    private static bool IsJavaHomeBinEntry(string entry)
    {
        var value = entry.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return value.Equals(@"%JAVA_HOME%\bin", StringComparison.OrdinalIgnoreCase);
    }
}
