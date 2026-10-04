using Admin.Host.Compose;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Stack;

public static class StackEndpoints
{
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

        app.MapPost("/api/stack/backend/up", (ComposeService compose) => TypedResults.Accepted((string?)null, JobSummary.Of(compose.Up())));

        app.MapPost("/api/stack/backend/down", Results<Accepted<JobSummary>, ProblemHttpResult> (DownRequest request, ComposeService compose) =>
        {
            if (request.WipeVolumes && request.Confirm != "down -v")
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: "Wiping volumes destroys databases and broker state. Send confirm: \"down -v\".");
            }

            return TypedResults.Accepted((string?)null, JobSummary.Of(compose.Down(request.WipeVolumes)));
        });

        // Reset wipes volumes, so it takes the confirmation "Down and wipe" takes (spec §8), spelled the same.
        app.MapPost("/api/stack/backend/reset", Results<Accepted<JobSummary>, ProblemHttpResult> (ResetRequest request, ResetService reset) =>
            request.Confirm != "down -v"
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: "Reset wipes volumes, which destroys databases and broker state. Send confirm: \"down -v\".")
                : TypedResults.Accepted((string?)null, JobSummary.Of(reset.Start())));

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
