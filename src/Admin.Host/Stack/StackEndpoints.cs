using Admin.Host.Compose;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Stack;

public static class StackEndpoints
{
    private const string Up = "up";

    private const string Down = "down";

    private const string DownWipe = "down -v";

    public static IEndpointRouteBuilder MapStack(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stack", async (ComposeService compose, FrontendSupervisor frontend, PlatformProbe probe, ScenarioLeaseService leases, CancellationToken cancellationToken) =>
        {
            Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
            Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);

            return TypedResults.Ok(new StackView(await backend, frontend.Status(), await reachability, leases.Current));
        });

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

        app.MapPost("/api/logs/follow", async (FollowRequest request, LogFollower follower, CancellationToken cancellationToken) =>
            TypedResults.Accepted((string?)null, JobSummary.Of(await follower.StartAsync(request.Services ?? [], cancellationToken))));

        app.MapPost("/api/logs/follow/{id}/stop", async (string id, LogFollower follower, CancellationToken cancellationToken) =>
        {
            await follower.StopAsync(id, cancellationToken);

            return TypedResults.NoContent();
        });

        return app;
    }
}

public sealed record StackView(ComposeStatus Backend, FrontendStatus Frontend, IReadOnlyList<Reachability> Reachability, LeaseView? Lease);

/// <summary>One or more changes from <see cref="ScenarioControl.Table"/>, how long to hold them, and a label the Stack screen shows beside them.</summary>
public sealed record LeaseRequest(string[]? Changes, int HoldSeconds, string? Label);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record ResetRequest(string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
