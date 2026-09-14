using Admin.Host.Compose;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Stack;

public static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStack(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stack", async (ComposeService compose, PlatformProbe probe, CancellationToken cancellationToken) =>
        {
            Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
            Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);

            return TypedResults.Ok(new StackView(await backend, await reachability));
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

        app.MapPost("/api/logs/follow", (FollowRequest request, ComposeService compose) =>
            TypedResults.Accepted((string?)null, JobSummary.Of(compose.FollowLogs(request.Services ?? []))));

        return app;
    }
}

public sealed record StackView(ComposeStatus Backend, IReadOnlyList<Reachability> Reachability);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
