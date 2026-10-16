using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>What the Stack screen and a Scenario see of a held lease.</summary>
public sealed record LeaseView(
    string Id,
    string Change,
    string Title,
    string Service,
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

/// <summary>
/// A Scenario's hold on the stack (spec §5.3, <em>Scenario service control</em>): one change from
/// <see cref="ScenarioControl.Table"/>, made under <see cref="ComposeGate"/> and always undone. The lease is journaled
/// before the first command runs and the journal removed only after the restore is verified; the restore runs on a failed
/// apply, on a cancelled request, at the lease's deadline, on request, and at the next host start from the journal, so a
/// host that was killed leaves nothing changed past its next start. A killed host cannot run its own restore, which is the
/// limit the spec states: until the next start the stack stays as the run left it, and the Stack screen shows the lease.
/// </summary>
public sealed partial class ScenarioLeaseService(
    ComposeService compose,
    ComposeGate gate,
    string journalPath,
    TimeProvider time,
    ILogger<ScenarioLeaseService> logger) : IDisposable
{
    /// <summary>How long an apply or a restore may take: <c>--wait</c> holds the job until the service is healthy.</summary>
    public static readonly TimeSpan JobCap = TimeSpan.FromMinutes(3);

    /// <summary>How long after a restore that failed the host tries it again, so a lease never waits for a person to remember it.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim operations = new(1, 1);
    private volatile Held? current;
    private ITimer? deadline;

    public LeaseView? Current => current is { } held ? View(held) : null;

    public LeaseState State() =>
        new(Current, [.. ScenarioControl.Table.Values.Select(c => new ChangeView(c.Id, c.Title, c.Service, c.Description, (int)c.MaxHold.TotalSeconds))]);

    public async Task<AcquireResult> AcquireAsync(string changeId, TimeSpan hold, string? label, CancellationToken cancellationToken)
    {
        if (!ScenarioControl.Table.TryGetValue(changeId, out ScenarioChange? change))
        {
            return new(AcquireOutcome.UnknownChange, null, null, $"'{changeId}' is not a change a Scenario may make: {string.Join(", ", ScenarioControl.Table.Keys)}.");
        }

        if (hold <= TimeSpan.Zero || hold > change.MaxHold)
        {
            return new(AcquireOutcome.BadHold, null, null, $"A lease for {change.Id} may be held for 1 to {change.MaxHold.TotalSeconds:0} seconds.");
        }

        await operations.WaitAsync(cancellationToken);

        try
        {
            DateTimeOffset now = time.GetUtcNow();
            Journal journal = new(Guid.NewGuid().ToString("N"), change.Id, label, now, now + hold);
            Job? apply = null;

            // The journal is written inside the gate's start, so it is on disk before the first command of the change runs.
            (bool held, Claim claim) = gate.HoldLease(() =>
            {
                WriteJournal(journal);
                apply = change.Apply(compose);

                return apply;
            });

            if (!held)
            {
                return new(AcquireOutcome.Busy, null, claim, null);
            }

            current = new Held(journal, change, apply!.Id, null);

            // Armed as soon as the lease is held, from the journal's own deadline, so a lease never outlives the hold it was
            // asked for by the length of the change, and one whose undo later fails still has a timer.
            ArmDeadline(journal.ExpiresAt - time.GetUtcNow());

            string? failure = null;

            try
            {
                int exitCode = await apply!.Completion.WaitAsync(JobCap, time, cancellationToken);

                if (exitCode != 0)
                {
                    failure = $"{apply.Spec.CommandLine} exited with {exitCode}.";
                }
            }
            catch (TimeoutException)
            {
                failure = $"{apply!.Spec.CommandLine} did not finish within {JobCap.TotalSeconds:0} seconds.";

                // The restore must not run beside the command it undoes.
                await compose.AbandonAsync(apply);
            }
            catch (OperationCanceledException)
            {
                // The request went away mid-change: end the change, then undo it, before the cancellation is let through.
                await compose.AbandonAsync(apply!);
                await RestoreHeldAsync(current!, change.Restore(compose));

                throw;
            }

            if (failure is not null)
            {
                RestoreResult undone = await RestoreHeldAsync(current!, change.Restore(compose));

                return new(AcquireOutcome.Failed, null, null, undone.Outcome == RestoreOutcome.Restored
                    ? $"{failure} The change was undone."
                    : $"{failure} It could not be undone: {undone.Detail} The lease is still held; Restore it from the Stack screen.");
            }

            return new(AcquireOutcome.Held, View(current!), null, null);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>Undoes the held change; nothing held is an answer, not an error, so a second call is harmless.</summary>
    public async Task<RestoreResult> RestoreAsync()
    {
        await operations.WaitAsync();

        try
        {
            return current is { } held ? await RestoreHeldAsync(held, held.Change.Restore(compose)) : new(RestoreOutcome.NothingHeld, null);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>
    /// The journal a previous host left, restored: the lease the host died holding. Holds the gate for the restore, so
    /// nothing is brought up or taken down beside it, and releases it when the service is back.
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

            if (journal is null || !ScenarioControl.Table.TryGetValue(journal.Change, out ScenarioChange? change))
            {
                JournalUnknown(logger, journalPath);
                return;
            }

            JournalFound(logger, journal.Id, journal.Change);
            Job? restore = null;
            (bool held, _) = gate.HoldLease(() => restore = change.Restore(compose));

            if (held)
            {
                Held lease = new(journal, change, null, null);
                current = lease;
                await FinishRestoreAsync(lease, restore!);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    public void Dispose()
    {
        deadline?.Dispose();
        operations.Dispose();
    }

    private Task<RestoreResult> RestoreHeldAsync(Held held, Job restore) => FinishRestoreAsync(held, restore);

    /// <summary>
    /// Waits for a restore, checks the service is running again, and only then drops the journal, the deadline and the
    /// gate. A restore that did not work keeps all three and says why, so it is never silently given up.
    /// </summary>
    private async Task<RestoreResult> FinishRestoreAsync(Held held, Job restore)
    {
        string? failure = null;

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
        }

        if (failure is null && !await ServiceRunningAsync(held.Change.Service))
        {
            failure = $"{held.Change.Service} is not running after the restore.";
        }

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
        DeleteJournal();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host found the lease {Id} ({Change}) in its journal and is restoring it.")]
    private static partial void JournalFound(ILogger logger, string id, string change);

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

    private static LeaseView View(Held held) => new(
        held.Journal.Id,
        held.Change.Id,
        held.Change.Title,
        held.Change.Service,
        held.Journal.Label,
        held.Journal.Since,
        held.Journal.ExpiresAt,
        held.ApplyJob,
        held.RestoreError);

    /// <summary>What the journal file holds: enough to restore a lease the host died holding.</summary>
    private sealed record Journal(string Id, string Change, string? Label, DateTimeOffset Since, DateTimeOffset ExpiresAt);

    private sealed record Held(Journal Journal, ScenarioChange Change, string? ApplyJob, string? RestoreError);
}
