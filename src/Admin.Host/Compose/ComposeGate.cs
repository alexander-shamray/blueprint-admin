using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Compose;

/// <summary>
/// One job at a time that stops or starts the stack's services: Up, Down, the reset, the projection rebuild, a game-day
/// cause or restore, the stress run, and a Scenario's lease (spec §5.3). Each
/// kept a gate of its own, or none, so a rebuild's <c>stop web-bff</c> could land in the middle of a reset's
/// <c>down -v</c> and its <c>start web-bff</c> bring the BFF up into a half-reset stack. A Compose read changes
/// nothing and takes no claim.
/// </summary>
public sealed class ComposeGate
{
    /// <summary>The kind of a Scenario's lease (spec §5.3, <em>Scenario service control</em>), as a refusal names it.</summary>
    public const string LeaseKind = "scenario lease";

    private readonly Lock gate = new();
    private Claim? current;
    private Claim? lease;

    /// <summary>
    /// Runs <paramref name="start"/> and holds the gate with its job, unless a job is still running under it or a lease is
    /// held; that one is returned instead, with its own kind, and nothing is started.
    /// </summary>
    public Claim Claim(string kind, Func<Job> start)
    {
        lock (gate)
        {
            if (lease is { } held)
            {
                return held;
            }

            if (current is { Job.Status.State: JobState.Running } running)
            {
                return running;
            }

            current = new Claim(start(), kind);

            return current;
        }
    }

    /// <summary>
    /// Runs <paramref name="start"/> and holds the gate as a lease until <see cref="ReleaseLease"/>, which outlives the
    /// job that began it: the claim a Scenario keeps while a service is stopped or recreated. Refused, with the claim that
    /// refuses it, while a job is running or a lease is already held; nothing is started then.
    /// </summary>
    public (bool Held, Claim Claim) HoldLease(Func<Job> start)
    {
        lock (gate)
        {
            if (lease is { } held)
            {
                return (false, held);
            }

            if (current is { Job.Status.State: JobState.Running } running)
            {
                return (false, running);
            }

            lease = new Claim(start(), LeaseKind);

            return (true, lease);
        }
    }

    public void ReleaseLease()
    {
        lock (gate)
        {
            lease = null;
        }
    }
}

/// <summary>A job a start began or found still going, with the kind it is.</summary>
public sealed record Claim(Job Job, string Kind)
{
    /// <summary>
    /// The job, when it is of the kind asked for, whether just started or still going (a second click is idempotent).
    /// Another kind still going is refused, naming it: a confirmed request that came back as someone else's job would
    /// be a request swallowed without a word.
    /// </summary>
    public Results<Accepted<JobSummary>, ProblemHttpResult> Answer(string wanted) =>
        Kind == wanted
            ? TypedResults.Accepted((string?)null, JobSummary.Of(Job))
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A run is already going",
                detail: Kind == ComposeGate.LeaseKind
                    ? $"A Scenario holds a lease (job {Job.Id}), so this {wanted} was not started. Restore it on the Stack screen, or wait for its deadline."
                    : $"A {Kind} (job {Job.Id}) is still running, so this {wanted} was not started. Wait for it to end.");
}
