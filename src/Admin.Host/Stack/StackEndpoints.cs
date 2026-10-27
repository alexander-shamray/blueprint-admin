using System.Text.RegularExpressions;
using Admin.Host.Compose;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Stack;

public static partial class StackEndpoints
{
    private const string Up = "up";

    private const string Down = "down";

    private const string DownWipe = "down -v";

    public static IEndpointRouteBuilder MapStack(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stack", async (ComposeService compose, FrontendSupervisor frontend, PlatformProbe probe, ScenarioLeaseService leases, LoadRunService loadRun, StressRunService stressRun, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ReadAsync(compose, frontend, probe, leases, loadRun, stressRun, cancellationToken)));

        // The backend's SLO run as one job (spec §5.3): a run still going answers a second Run with itself, as an Up does, and
        // Stop ends it at the step it is in. The stress run's traffic would be read as its own, so one going refuses it.
        app.MapPost("/api/stack/load-run", Results<Accepted<JobSummary>, ProblemHttpResult> (LoadRunService loadRun, StressRunService stressRun) =>
            stressRun.Current is { State: JobState.Running } stress
                ? Busy("load run", "stress run", stress)
                : TypedResults.Accepted((string?)null, JobSummary.Of(loadRun.Start())));

        app.MapPost("/api/stack/load-run/stop", async Task<Results<Ok<JobSummary>, ProblemHttpResult>> (LoadRunService loadRun) =>
            await loadRun.StopAsync() is Job stopped
                ? TypedResults.Ok(JobSummary.Of(stopped))
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No load run going", detail: "There is no load run to stop."));

        // The backend's stress run as one job under ComposeGate (spec §5.3), since its up recreates services: one going answers a
        // second Run with itself and an Up, a Down, a reset or a lease is refused while it runs. A load run going refuses it.
        app.MapPost("/api/stack/stress-run", Results<Accepted<JobSummary>, ProblemHttpResult> (StressRunService stressRun, LoadRunService loadRun) =>
            loadRun.Current is { State: JobState.Running } load
                ? Busy("stress run", "load run", load)
                : stressRun.Start().Answer(StressRunService.Kind));

        app.MapPost("/api/stack/stress-run/stop", async Task<Results<Ok<JobSummary>, ProblemHttpResult>> (StressRunService stressRun) =>
            await stressRun.StopAsync() is Job stopped
                ? TypedResults.Ok(JobSummary.Of(stopped))
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No stress run going", detail: "There is no stress run to stop."));

        // A Scenario's lease (spec §5.3): a change from ScenarioControl.Table held under ComposeGate, journaled, and always
        // undone. The acquire waits for the change, so the answer is the lease held or why it was not; a request that goes
        // away mid-change restores it before it ends.
        app.MapGet("/api/stack/lease", (ScenarioLeaseService leases) => TypedResults.Ok(leases.State()));

        app.MapPost("/api/stack/lease", async Task<Results<Ok<LeaseView>, ProblemHttpResult>> (LeaseRequest request, ScenarioLeaseService leases, CancellationToken cancellationToken) =>
        {
            AcquireResult result = await leases.AcquireAsync(request.Changes ?? [], TimeSpan.FromSeconds(request.HoldSeconds), request.Label, cancellationToken);

            return result.Outcome switch
            {
                AcquireOutcome.Held => TypedResults.Ok(result.Lease!),
                AcquireOutcome.UnknownChange or AcquireOutcome.BadHold => TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest, title: "Lease refused", detail: result.Detail),
                AcquireOutcome.Busy => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "A run is already going",
                    detail: result.Busy!.Kind == ComposeGate.LeaseKind
                        ? $"A Scenario already holds a lease (job {result.Busy.Job.Id}). Restore it first."
                        : $"A {result.Busy.Kind} (job {result.Busy.Job.Id}) is still running, so no lease was taken. Wait for it to end."),
                _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The change failed", detail: result.Detail),
            };
        });

        // One more change while the lease is held (spec §5.3), for a cause that must follow another. The lease's own deadline
        // stands; a change that fails undoes every change the lease made.
        app.MapPost("/api/stack/lease/changes", async Task<Results<Ok<LeaseView>, ProblemHttpResult>> (LeaseChangeRequest request, ScenarioLeaseService leases, CancellationToken cancellationToken) =>
        {
            AddResult result = await leases.AddChangeAsync(request.Change ?? string.Empty, cancellationToken);

            return result.Outcome switch
            {
                AddOutcome.Held => TypedResults.Ok(result.Lease!),
                AddOutcome.UnknownChange => TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest, title: "Change refused", detail: result.Detail),
                AddOutcome.NothingHeld => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "No lease to add to", detail: result.Detail),
                _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The change failed", detail: result.Detail),
            };
        });

        app.MapPost("/api/stack/lease/restore", async Task<Results<Ok<LeaseState>, ProblemHttpResult>> (ScenarioLeaseService leases) =>
        {
            RestoreResult result = await leases.RestoreAsync();

            return result.Outcome == RestoreOutcome.Failed
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The restore failed",
                    detail: $"{result.Detail} The lease is still held; Restore tries again.")
                : TypedResults.Ok(leases.State());
        });

        app.MapGet("/api/stack/doctor", async (WorkstationDoctor doctor, CancellationToken cancellationToken) =>
            TypedResults.Ok(await doctor.ReadAsync(cancellationToken)));

        // Up, Down and the reset each claim ComposeGate, as a projection rebuild does: one of them still going answers a
        // second of its kind with itself and refuses any other kind with 409, naming the job in the way.
        app.MapPost("/api/stack/backend/up", Results<Accepted<JobSummary>, ProblemHttpResult> (ComposeService compose, ComposeGate gate) =>
            gate.Claim(Up, compose.Up).Answer(Up));

        app.MapPost("/api/stack/backend/down", Results<Accepted<JobSummary>, ProblemHttpResult> (DownRequest request, ComposeService compose, ComposeGate gate) =>
        {
            if (request.WipeVolumes && request.Confirm != "down -v")
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: "Wiping volumes destroys databases and broker state. Send confirm: \"down -v\".");
            }

            string kind = request.WipeVolumes ? DownWipe : Down;

            return gate.Claim(kind, () => compose.Down(request.WipeVolumes)).Answer(kind);
        });

        // Reset wipes volumes, so it takes the confirmation "Down and wipe" takes (spec §8), spelled the same.
        app.MapPost("/api/stack/backend/reset", Results<Accepted<JobSummary>, ProblemHttpResult> (ResetRequest request, ResetService reset) =>
            request.Confirm != "down -v"
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: "Reset wipes volumes, which destroys databases and broker state. Send confirm: \"down -v\".")
                : reset.Start().Answer(ResetService.Kind));

        // A name goes into `docker compose logs -f --tail 200 <names>`, so one that starts with '-' would be an option.
        app.MapPost("/api/logs/follow", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FollowRequest request, LogFollower follower, CancellationToken cancellationToken) =>
            (request.Services ?? []).FirstOrDefault(s => !ServiceName().IsMatch(s)) is { } refused
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Not a service name",
                    detail: $"'{refused}' is not a Compose service name (letters, digits, '.', '_' and '-', not starting with '-').")
                : TypedResults.Accepted((string?)null, JobSummary.Of(await follower.StartAsync(request.Services ?? [], cancellationToken))));

        app.MapPost("/api/logs/follow/{id}/stop", async (string id, LogFollower follower, CancellationToken cancellationToken) =>
        {
            await follower.StopAsync(id, cancellationToken);

            return TypedResults.NoContent();
        });

        return app;
    }

    /// <summary>The refusal of one k6 run while the other goes: two staircases on one stack would each read the other's load.</summary>
    private static ProblemHttpResult Busy(string wanted, string going, Job job) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "A run is already going",
            detail: $"A {going} (job {job.Id}) is still running, so this {wanted} was not started. Stop it or wait for it to end.");

    /// <summary>
    /// The Stack screen's read: the backend's services, the frontend, what answers on its port, the lease, the last load run, the
    /// last stress run and which services run on the stress override. <c>GET /api/stack</c> and the assistant's
    /// <c>stack_status</c> both call it, so the tool reads what the screen reads.
    /// </summary>
    public static async Task<StackView> ReadAsync(
        ComposeService compose,
        FrontendSupervisor frontend,
        PlatformProbe probe,
        ScenarioLeaseService leases,
        LoadRunService loadRun,
        StressRunService stressRun,
        CancellationToken cancellationToken)
    {
        Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
        Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);
        JobSummary? lastRun = loadRun.Current is { } run ? JobSummary.Of(run) : null;
        ComposeStatus services = await backend;

        return new StackView(services, frontend.Status(), await reachability, leases.Current, lastRun, stressRun.View(), StressRunService.OnOverride(services));
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex ServiceName();
}

/// <summary>
/// <see cref="StressOverride"/> names each service running on the stress override (<see cref="StressRunService.OnOverride"/>),
/// empty when none is: the Stack screen says so while any is, since a stack left on it has its gateway's rate limits lifted.
/// </summary>
public sealed record StackView(
    ComposeStatus Backend,
    FrontendStatus Frontend,
    IReadOnlyList<Reachability> Reachability,
    LeaseView? Lease,
    JobSummary? LoadRun,
    StressRunView? StressRun,
    IReadOnlyList<string> StressOverride);

/// <summary>One or more changes from <see cref="ScenarioControl.Table"/>, how long to hold them, and a label the Stack screen shows beside them.</summary>
public sealed record LeaseRequest(string[]? Changes, int HoldSeconds, string? Label);

public sealed record LeaseChangeRequest(string? Change);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record ResetRequest(string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
