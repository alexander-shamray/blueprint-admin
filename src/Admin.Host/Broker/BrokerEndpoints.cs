using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Broker;

public static class BrokerEndpoints
{
    public static IEndpointRouteBuilder MapBroker(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/broker/queues", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.QueuesAsync(cancellationToken)));

        app.MapGet("/api/broker/exchanges", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.ExchangesAsync(cancellationToken)));

        app.MapGet("/api/broker/permissions", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.PermissionsAsync(cancellationToken)));

        app.MapGet("/api/broker/projection", () => TypedResults.Ok(ProjectionRebuildService.Plan()));

        // A repair deletes nothing, so it takes no confirmation; a rebuild deletes the projection and takes the typed
        // one the Stack screen's wipe takes (spec §8), spelled as the command.
        app.MapPost("/api/broker/projection/repair", Results<Accepted<JobSummary>, ProblemHttpResult> (ProjectionRebuildService rebuild) =>
            Answer(rebuild.Start(RebuildMode.Repair), RebuildMode.Repair));

        app.MapPost("/api/broker/projection/rebuild", Results<Accepted<JobSummary>, ProblemHttpResult> (RebuildRequest request, ProjectionRebuildService rebuild) =>
            request.Confirm != ProjectionRebuildService.RebuildConfirm
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: $"A rebuild deletes the BFF's order projection first. Send confirm: \"{ProjectionRebuildService.RebuildConfirm}\".")
                : Answer(rebuild.Start(RebuildMode.Rebuild), RebuildMode.Rebuild));

        return app;
    }

    /// <summary>
    /// The run that was started, or the same kind still going. A different kind still going is refused, naming it: a
    /// confirmed rebuild that came back as someone else's repair would be a request swallowed without a word.
    /// </summary>
    internal static Results<Accepted<JobSummary>, ProblemHttpResult> Answer(Started started, RebuildMode wanted) =>
        started.Mode == wanted
            ? TypedResults.Accepted((string?)null, JobSummary.Of(started.Job))
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A run is already going",
                detail: $"A {started.Mode.ToString().ToLowerInvariant()} (job {started.Job.Id}) is still running, so this {wanted.ToString().ToLowerInvariant()} was not started. Wait for it to end.");
}
