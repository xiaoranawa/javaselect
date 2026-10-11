namespace JavaSelect.Core;

public sealed class PathRepairAttemptPolicy
{
    private readonly HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);

    public bool CanAttempt(PathRepairPlan plan, bool recovery, bool readOnly = false)
    {
        if (readOnly || !plan.CanRepair || !plan.RequiresElevation || plan.RemovedEntries is not { Count: > 0 }) return false;
        return !recovery || !ConflictDirectories(plan).Any(attempted.Contains);
    }

    public void RecordAttempt(PathRepairPlan plan) => attempted.UnionWith(ConflictDirectories(plan));
    public void SelectionSucceeded() => attempted.Clear();

    private static IEnumerable<string> ConflictDirectories(PathRepairPlan plan)
        => plan.RemovedEntries.Select(x => Path.TrimEndingDirectorySeparator(x.ResolvedDirectory));
}

public sealed record PathRepairAttemptResult(EnvironmentSwitchResult Result, bool Attempted = false, bool Cancelled = false);

public static class PathRepairWorkflow
{
    public static async Task<PathRepairAttemptResult> RunAsync(PathRepairPlan plan, EnvironmentSwitchResult initial,
        PathRepairAttemptPolicy policy, bool recovery, bool readOnly,
        Func<PathRepairPlan, CancellationToken, Action, Task<bool>> repair,
        Func<string, CancellationToken, Task<EnvironmentSwitchResult>> retrySelection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (initial.Success || initial.ConflictPath is null || !policy.CanAttempt(plan, recovery, readOnly)) return new(initial);
        var started = false;
        if (!await repair(plan, cancellationToken, () => { policy.RecordAttempt(plan); started = true; }))
            return new(initial, Attempted: started, Cancelled: started);
        // Once the system repair commits, finish the user selection before shutdown.
        return new(await retrySelection(plan.ExpectedFingerprint, CancellationToken.None), Attempted: true);
    }
}
