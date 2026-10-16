using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Frontend;

public static class FrontendEndpoints
{
    public static IEndpointRouteBuilder MapFrontend(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/stack/frontend/start", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, RepoPaths paths, CancellationToken cancellationToken) =>
        {
            FrontendStartResult result = await supervisor.StartAsync(cancellationToken);

            return result.Outcome switch
            {
                FrontendStartOutcome.Started => TypedResults.Accepted((string?)null, JobSummary.Of(result.Job!)),
                FrontendStartOutcome.AlreadyRunning => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Frontend already running",
                    detail: $"npm start is job {result.Job!.Id}. Stop it first."),
                _ => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Frontend dependencies not installed",
                    detail: $"{paths.FrontendDir} has no node_modules. Run npm ci there; the console does not."),
            };
        });

        app.MapPost("/api/stack/frontend/stop", async Task<Results<Ok<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, CancellationToken cancellationToken) =>
            await supervisor.StopAsync(cancellationToken) is Job stopped
                ? TypedResults.Ok(JobSummary.Of(stopped))
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Frontend not running", detail: "There is no npm start job to stop."));

        app.MapPost("/api/stack/frontend/device-build", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, RepoPaths paths, CancellationToken cancellationToken) =>
        {
            FrontendStartResult result = await supervisor.StartDeviceBuildAsync(cancellationToken);

            return result.Outcome switch
            {
                FrontendStartOutcome.Started => TypedResults.Accepted((string?)null, JobSummary.Of(result.Job!)),
                FrontendStartOutcome.AlreadyRunning => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Device build already running",
                    detail: $"npm run {FrontendSupervisor.DeviceScript} is job {result.Job!.Id}. It ends by itself."),
                _ => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Frontend dependencies not installed",
                    detail: $"{paths.FrontendDir} has no node_modules. Run npm ci there; the console does not."),
            };
        });

        app.MapGet("/api/stack/frontend/device-reads", async (DeviceReads reads, CancellationToken cancellationToken) =>
            TypedResults.Ok(await reads.ReadAsync(cancellationToken)));

        return app;
    }
}
