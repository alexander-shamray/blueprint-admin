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

        app.MapGet("/api/broker/dead-letters", (DeadLetterService deadLetters) => TypedResults.Ok(deadLetters.Plan()));

        // Reading a parked queue puts every message back, flagged redelivered, so it takes no confirmation.
        app.MapGet("/api/broker/dead-letters/{queue}", async Task<Results<Ok<DeadLetterInspection>, ProblemHttpResult>> (
            string queue, DeadLetterService deadLetters, CancellationToken cancellationToken) =>
        {
            if (!DeadLetterService.IsParkedQueue(queue))
            {
                return NotParked(queue);
            }

            return await deadLetters.InspectAsync(queue, cancellationToken) is { } inspection ? TypedResults.Ok(inspection) : Busy();
        });

        // A dry run moves nothing; the real run takes each message off the queue, so it takes the typed confirmation
        // a wipe does (spec §8), spelled as the flag.
        app.MapPost("/api/broker/dead-letters/{queue}/{verb}", Results<Accepted<JobSummary>, ProblemHttpResult> (
            string queue, string verb, DeadLetterMoveRequest request, DeadLetterService deadLetters) =>
        {
            if (!DeadLetterService.IsParkedQueue(queue))
            {
                return NotParked(queue);
            }

            if (!Enum.TryParse(verb, ignoreCase: true, out DeadLetterVerb parsed) || !Enum.IsDefined(parsed) || verb != DeadLetterService.Verb(parsed))
            {
                return Refused("Unknown verb", $"'{verb}' is neither replay nor discard.");
            }

            if (request.MessageIds is not { Count: > 0 } ids || !ids.All(DeadLetterService.IsMessageId))
            {
                return Refused("Messages required", "Name each message by the message_id an inspection gave it.");
            }

            if (request.Execute && request.Confirm != DeadLetterService.ExecuteConfirm)
            {
                return Refused(
                    "Confirmation required",
                    $"The real {verb} takes each message off {queue}. Send confirm: \"{DeadLetterService.ExecuteConfirm}\".");
            }

            return deadLetters.Start(parsed, queue, ids, request.Execute) is { } job
                ? TypedResults.Accepted((string?)null, JobSummary.Of(job))
                : Busy();
        });

        return app;
    }

    private static ProblemHttpResult NotParked(string queue) =>
        Refused("Not a parked queue", $"'{queue}' is not an endpoint's _error or _skipped queue, the only queues the tool reads.");

    private static ProblemHttpResult Busy() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "A dead-letter run is already going",
        detail: "The operator's password is set for one run at a time. Wait for that run to end.");

    private static ProblemHttpResult Refused(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

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
