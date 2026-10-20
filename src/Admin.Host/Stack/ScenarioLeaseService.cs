using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>What the Stack screen and a Scenario see of a held lease.</summary>
public sealed record LeaseView(
    string Id,
    IReadOnlyList<string> Changes,
    string Title,
    IReadOnlyList<string> Services,
    string? Label,
    DateTimeOffset Since,
    DateTimeOffset ExpiresAt,
    string? ApplyJob,
    string? RestoreError);

/// <summary>One change a lease may be asked for, as the Scenario page names it before Run.</summary>
public sealed record ChangeView(string Id, string Title, string Service, string Description, int MaxHoldSeconds);

public sealed record LeaseState(LeaseView? Lease, IReadOnlyList<ChangeView> Changes);

public enum AcquireOutcome
{
    Held,
    UnknownChange,
    BadHold,
    Busy,
    Failed,
}

/// <summary>An acquire's answer: the lease when held, the claim that refused it when busy, and why when it failed.</summary>
public sealed record AcquireResult(AcquireOutcome Outcome, LeaseView? Lease, Claim? Busy, string? Detail);

public enum RestoreOutcome
{
    NothingHeld,
    Restored,
    Failed,
}

public sealed record RestoreResult(RestoreOutcome Outcome, string? Detail);

public enum AddOutcome
{
    Held,
    UnknownChange,
    NothingHeld,
    Failed,
}

/// <summary>An add's answer: the lease with the change made, and why when it was refused or failed.</summary>
public sealed record AddResult(AddOutcome Outcome, LeaseView? Lease, string? Detail);

/// <summary>
/// A Scenario's hold on the stack (spec §5.3, <em>Scenario service control</em>): one or more changes from
/// <see cref="ScenarioControl.Table"/>, made in order under <see cref="ComposeGate"/> and always undone, last first. The lease
/// is journaled before the first command runs and the journal removed only after every restore is verified; the restore runs on
/// a failed apply, on a cancelled request, at the lease's deadline, on request, and at the next host start from the journal, so
/// a host that was killed leaves nothing changed past its next start. A killed host cannot run its own restore, which is the
/// limit the spec states: until the next start the stack stays as the run left it, and the Stack screen shows the lease.
/// </summary>
public sealed partial class ScenarioLeaseService(
    ComposeService compose,
    ComposeGate gate,
    string journalPath,
    TimeProvider time,
    ILogger<ScenarioLeaseService> logger) : IDisposable
{
    /// <summary>How long one apply or one restore may take: <c>--wait</c> holds the job until the service is healthy.</summary>
    public static readonly TimeSpan JobCap = TimeSpan.FromMinutes(3);

    /// <summary>How long after a restore that failed the host tries it again, so a lease never waits for a person to remember it.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim operations = new(1, 1);
    private volatile Held? current;
    private ITimer? deadline;
    private ITimer? reconcileRetry;

    public LeaseView? Current => current is { } held ? View(held) : null;

    public LeaseState State() =>
        new(Current, [.. ScenarioControl.Table.Values.Select(c => new ChangeView(c.Id, c.Title, c.Service, c.Description, (int)c.MaxHold.TotalSeconds))]);

    public async Task<AcquireResult> AcquireAsync(IReadOnlyList<string> changeIds, TimeSpan hold, string? label, CancellationToken cancellationToken)
    {
        if (changeIds.Count == 0 || changeIds.Distinct(StringComparer.Ordinal).Count() != changeIds.Count
            || changeIds.Any(id => !ScenarioControl.Table.ContainsKey(id)))
        {
            string named = changeIds.Count == 0 ? "none" : string.Join(", ", changeIds);

            return new(AcquireOutcome.UnknownChange, null, null, $"A lease takes one or more of {string.Join(", ", ScenarioControl.Table.Keys)}, each once; this asked for {named}.");
        }

        ScenarioChange[] changes = [.. changeIds.Select(id => ScenarioControl.Table[id])];
        TimeSpan longest = changes.Min(c => c.MaxHold);

        if (hold <= TimeSpan.Zero || hold > longest)
        {
            return new(AcquireOutcome.BadHold, null, null, $"A lease for {string.Join(" and ", changeIds)} may be held for 1 to {longest.TotalSeconds:0} seconds.");
        }

        await operations.WaitAsync(cancellationToken);

        try
        {
            DateTimeOffset now = time.GetUtcNow();
            Journal journal = new(Guid.NewGuid().ToString("N"), [.. changeIds], null, label, now, now + hold);
            Job? first = null;

            // The journal is written inside the gate's start, so it is on disk before the first command of the first change runs.
            (bool held, Claim claim) = gate.HoldLease(() =>
            {
                WriteJournal(journal);
                first = changes[0].Apply(compose);

                return first;
            });

            if (!held)
            {
                return new(AcquireOutcome.Busy, null, claim, null);
            }

            Held lease = new(journal, changes, first!.Id, null);
            current = lease;

            // Armed as soon as the lease is held, from the journal's own deadline, so a lease never outlives the hold it was
            // asked for by the length of its changes, and one whose undo later fails still has a timer.
            ArmDeadline(journal.ExpiresAt - time.GetUtcNow());

            string? failure = null;
            Job running = first!;

            try
            {
                for (int i = 0; i < changes.Length && failure is null; i++)
                {
                    running = i == 0 ? first! : changes[i].Apply(compose);
                    int exitCode = await running.Completion.WaitAsync(JobCap, time, cancellationToken);

                    if (exitCode != 0)
                    {
                        failure = $"{running.Spec.CommandLine} exited with {exitCode}.";
                    }
                }
            }
            catch (TimeoutException)
            {
                failure = $"{running.Spec.CommandLine} did not finish within {JobCap.TotalSeconds:0} seconds.";

                // The restore must not run beside the command it undoes.
                await compose.AbandonAsync(running);
            }
            catch (OperationCanceledException)
            {
                // The request went away mid-change: end the change, then undo them all, before the cancellation is let through.
                await compose.AbandonAsync(running);
                await FinishRestoreAsync(lease, null);

                throw;
            }

            if (failure is not null)
            {
                RestoreResult undone = await FinishRestoreAsync(lease, null);

                return new(AcquireOutcome.Failed, null, null, undone.Outcome == RestoreOutcome.Restored
                    ? $"{failure} The changes were undone."
                    : $"{failure} They could not be undone: {undone.Detail} The lease is still held; Restore it from the Stack screen.");
            }

            return new(AcquireOutcome.Held, View(lease), null, null);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>
    /// Makes one more change while the lease is held, for a cause that must follow another: the release timeout needs Inventory
    /// stopped after its stock has reserved, which a lease taking its changes together cannot do. The change joins the journal
    /// before it runs, so a host killed mid-change restores it at the next start; the deadline is the one the lease was taken
    /// for and does not move; and a change that fails undoes the whole lease, as a failed acquire does.
    /// </summary>
    public async Task<AddResult> AddChangeAsync(string changeId, CancellationToken cancellationToken)
    {
        if (!ScenarioControl.Table.TryGetValue(changeId, out ScenarioChange? change))
        {
            return new(AddOutcome.UnknownChange, null, $"A lease takes one of {string.Join(", ", ScenarioControl.Table.Keys)}; this asked for {changeId}.");
        }

        await operations.WaitAsync(cancellationToken);

        try
        {
            if (current is not { RestoreError: null } held)
            {
                return new(AddOutcome.NothingHeld, null, current is null ? "No lease is held, so there is nothing to add a change to." : "The held lease is being restored.");
            }

            // The deadline is the lease's own and does not move, so a change added at or past it would be made on a lease whose
            // restore is already due, and answered as held.
            if (time.GetUtcNow() >= held.Journal.ExpiresAt)
            {
                return new(AddOutcome.NothingHeld, null, "The lease has reached its deadline and is being restored.");
            }

            if (held.Changes.Any(c => c.Id == changeId))
            {
                return new(AddOutcome.UnknownChange, null, $"The lease already holds {changeId}.");
            }

            Journal journal = held.Journal with { Changes = [.. held.Journal.Changes ?? [], changeId], Change = null };
            WriteJournal(journal);
            Held lease = held with { Journal = journal, Changes = [.. held.Changes, change] };
            current = lease;

            string? failure = null;
            Job running = change.Apply(compose);

            try
            {
                int exitCode = await running.Completion.WaitAsync(JobCap, time, cancellationToken);

                if (exitCode != 0)
                {
                    failure = $"{running.Spec.CommandLine} exited with {exitCode}.";
                }
            }
            catch (TimeoutException)
            {
                failure = $"{running.Spec.CommandLine} did not finish within {JobCap.TotalSeconds:0} seconds.";
                await compose.AbandonAsync(running);
            }
            catch (OperationCanceledException)
            {
                await compose.AbandonAsync(running);
                await FinishRestoreAsync(lease, null);

                throw;
            }

            if (failure is not null)
            {
                RestoreResult undone = await FinishRestoreAsync(lease, null);

                return new(AddOutcome.Failed, null, undone.Outcome == RestoreOutcome.Restored
                    ? $"{failure} The changes were undone."
                    : $"{failure} They could not be undone: {undone.Detail} The lease is still held; Restore it from the Stack screen.");
            }

            return new(AddOutcome.Held, View(lease), null);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>Undoes the held changes; nothing held is an answer, not an error, so a second call is harmless.</summary>
    public async Task<RestoreResult> RestoreAsync()
    {
        await operations.WaitAsync();

        try
        {
            return current is { } held ? await FinishRestoreAsync(held, null) : new(RestoreOutcome.NothingHeld, null);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>
    /// The journal a previous host left, restored: the lease the host died holding. Holds the gate for the restore, so
    /// nothing is brought up or taken down beside it, and releases it when the services are back.
    /// </summary>
    public async Task ReconcileAsync()
    {
        await operations.WaitAsync();

        try
        {
            if (current is not null || !File.Exists(journalPath))
            {
                return;
            }

            Journal? journal = ReadJournal();
            string[] ids = journal is null ? [] : [.. journal.Changes ?? (journal.Change is null ? [] : [journal.Change])];

            if (journal is null || ids.Length == 0 || ids.Any(id => !ScenarioControl.Table.ContainsKey(id)))
            {
                JournalUnknown(logger, journalPath);
                return;
            }

            JournalFound(logger, journal.Id, string.Join(", ", ids));
            ScenarioChange[] changes = [.. ids.Select(id => ScenarioControl.Table[id])];
            Job? firstRestore = null;
            (bool held, _) = gate.HoldLease(() => firstRestore = changes[^1].Restore(compose));

            if (held)
            {
                Held lease = new(journal with { Changes = ids, Change = null }, changes, null, null);
                current = lease;
                await FinishRestoreAsync(lease, firstRestore);
            }
            else
            {
                // A job holds the gate (an Up that began before this ran, say). The journal stays, and nothing else would read it
                // again until the next host start, so try once more shortly.
                reconcileRetry?.Dispose();
                reconcileRetry = time.CreateTimer(_ => { _ = RetryReconcileAsync(); }, null, RetryAfter, Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    private async Task RetryReconcileAsync()
    {
        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            DeadlineFailed(logger, ex);
        }
    }

    public void Dispose()
    {
        deadline?.Dispose();
        reconcileRetry?.Dispose();
        operations.Dispose();
    }

    /// <summary>
    /// Undoes the lease's changes, last first: each restore is waited for and its service checked running again, and only when
    /// every one has been does the journal, the deadline and the gate go. A restore that did not work stops there, keeps all
    /// three and says why, so it is never silently given up. <paramref name="started"/> is the last change's restore when the
    /// caller already began it (the gate's start, at host start).
    /// </summary>
    private async Task<RestoreResult> FinishRestoreAsync(Held held, Job? started)
    {
        string? failure = null;

        for (int i = held.Changes.Count - 1; i >= 0 && failure is null; i--)
        {
            ScenarioChange change = held.Changes[i];
            Job restore = i == held.Changes.Count - 1 && started is not null ? started : change.Restore(compose);

            try
            {
                int exitCode = await restore.Completion.WaitAsync(JobCap, time, CancellationToken.None);

                if (exitCode != 0)
                {
                    failure = $"{restore.Spec.CommandLine} exited with {exitCode}.";
                }
            }
            catch (TimeoutException)
            {
                failure = $"{restore.Spec.CommandLine} did not finish within {JobCap.TotalSeconds:0} seconds.";
                await compose.AbandonAsync(restore);
            }

            if (failure is null && !await ServiceRunningAsync(change.Service))
            {
                failure = $"{change.Service} is not running after the restore.";
            }
        }

        // The journal goes last. One that cannot be deleted keeps the lease and the gate, as a restore that failed does: the retry
        // restores again, which is safe twice, and deletes it once the file is free.
        failure ??= TryDeleteJournal();

        if (failure is not null)
        {
            current = held with { RestoreError = failure };
            RestoreFailed(logger, held.Journal.Id, failure);

            // A lease whose undo failed is still held and still journaled, so the host tries again on its own.
            ArmDeadline(RetryAfter);

            return new(RestoreOutcome.Failed, failure);
        }

        deadline?.Dispose();
        deadline = null;
        current = null;
        gate.ReleaseLease();

        return new(RestoreOutcome.Restored, null);
    }

    private async Task<bool> ServiceRunningAsync(string service)
    {
        ComposeStatus status = await compose.PsAsync(CancellationToken.None);

        return status.Services.Any(s => s.Service == service && s.State == "running");
    }

    /// <summary>The lease restores itself at its deadline, so a page that went away without asking leaves nothing changed.</summary>
    private void ArmDeadline(TimeSpan due)
    {
        deadline?.Dispose();
        deadline = time.CreateTimer(state => { _ = RestoreAtDeadlineAsync(); }, null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private async Task RestoreAtDeadlineAsync()
    {
        try
        {
            RestoreResult result = await RestoreAsync();
            DeadlineReached(logger, result.Outcome);
        }
        catch (Exception ex)
        {
            DeadlineFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lease journal at {Path} names no change this host knows; it was left in place for a person to read.")]
    private static partial void JournalUnknown(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host found the lease {Id} ({Changes}) in its journal and is restoring it.")]
    private static partial void JournalFound(ILogger logger, string id, string changes);

    [LoggerMessage(Level = LogLevel.Error, Message = "The lease {Id} could not be restored: {Failure}")]
    private static partial void RestoreFailed(ILogger logger, string id, string failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "A lease reached its deadline: {Outcome}.")]
    private static partial void DeadlineReached(ILogger logger, RestoreOutcome outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "A lease reached its deadline and could not be restored.")]
    private static partial void DeadlineFailed(ILogger logger, Exception exception);

    private void WriteJournal(Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        string temporary = journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, Json));
        File.Move(temporary, journalPath, overwrite: true);
    }

    private Journal? ReadJournal()
    {
        try
        {
            return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void DeleteJournal()
    {
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }
    }

    private string? TryDeleteJournal()
    {
        try
        {
            DeleteJournal();

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The lease journal at {journalPath} could not be deleted: {ex.Message}";
        }
    }

    private static LeaseView View(Held held) => new(
        held.Journal.Id,
        [.. held.Changes.Select(c => c.Id)],
        string.Join(" + ", held.Changes.Select(c => c.Title)),
        [.. held.Changes.Select(c => c.Service).Distinct(StringComparer.Ordinal)],
        held.Journal.Label,
        held.Journal.Since,
        held.Journal.ExpiresAt,
        held.ApplyJob,
        held.RestoreError);

    /// <summary>
    /// What the journal file holds: enough to restore a lease the host died holding. <c>Change</c> is the single-change
    /// field the first version wrote, still read so a journal it left is restored rather than ignored.
    /// </summary>
    private sealed record Journal(string Id, string[]? Changes, string? Change, string? Label, DateTimeOffset Since, DateTimeOffset ExpiresAt);

    private sealed record Held(Journal Journal, IReadOnlyList<ScenarioChange> Changes, string? ApplyJob, string? RestoreError);
}
