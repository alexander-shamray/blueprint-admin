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
        app.MapPost("/api/broker/projection/repair", (ProjectionRebuildService rebuild) =>
            TypedResults.Accepted((string?)null, JobSummary.Of(rebuild.Start(RebuildMode.Repair))));

        app.MapPost("/api/broker/projection/rebuild", Results<Accepted<JobSummary>, ProblemHttpResult> (RebuildRequest request, ProjectionRebuildService rebuild) =>
            request.Confirm != ProjectionRebuildService.RebuildConfirm
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: $"A rebuild deletes the BFF's order projection first. Send confirm: \"{ProjectionRebuildService.RebuildConfirm}\".")
                : TypedResults.Accepted((string?)null, JobSummary.Of(rebuild.Start(RebuildMode.Rebuild))));

        return app;
    }
}
