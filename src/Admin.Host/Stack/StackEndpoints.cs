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
        app.MapGet("/api/stack", async (ComposeService compose, FrontendSupervisor frontend, PlatformProbe probe, CancellationToken cancellationToken) =>
        {
            Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
            Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);

            return TypedResults.Ok(new StackView(await backend, frontend.Status(), await reachability));
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

public sealed record StackView(ComposeStatus Backend, FrontendStatus Frontend, IReadOnlyList<Reachability> Reachability);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record ResetRequest(string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
