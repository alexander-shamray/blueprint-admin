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

        app.MapPost("/api/logs/follow", async (FollowRequest request, LogFollower follower, CancellationToken cancellationToken) =>
            TypedResults.Accepted((string?)null, JobSummary.Of(await follower.StartAsync(request.Services ?? [], cancellationToken))));

        return app;
    }
}

public sealed record StackView(ComposeStatus Backend, FrontendStatus Frontend, IReadOnlyList<Reachability> Reachability);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
