using Admin.Host.Compose;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Runbooks;

/// <summary>The Runbooks screen's reads and its two writes (spec §5.14).</summary>
public static class RunbookEndpoints
{
    public static IEndpointRouteBuilder MapRunbooks(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runbooks", async (GameDayService gameDay, CancellationToken cancellationToken) =>
            TypedResults.Ok(await gameDay.ListAsync(cancellationToken)));

        app.MapGet("/api/runbooks/{name}", Results<Ok<RunbookText>, ProblemHttpResult> (string name, GameDayService gameDay) =>
            gameDay.Text(name) is { } markdown
                ? TypedResults.Ok(new RunbookText(name, markdown))
                : TypedResults.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "No such runbook",
                    detail: $"'{name}' is not a file in the backend clone's docs/runbooks/."));

        // A cause is destructive to the local stack, so it takes the confirmation the tool's catalogue names for the phase (spec §8).
        app.MapPost("/api/runbooks/{name}/cause", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (
            string name, CauseRequest request, GameDayService gameDay, CancellationToken cancellationToken) =>
        {
            (RunbookEntry? entry, ProblemHttpResult? refused) = await ScriptedAsync(name, gameDay, cancellationToken);

            if (entry is null)
            {
                return refused!;
            }

            RunbookPhase? phase = request.Phase is null
                ? (entry.Phases.Count > 0 ? entry.Phases[0] : null)
                : entry.Phases.FirstOrDefault(p => p.Alert == request.Phase);

            if (phase is null)
            {
                return Refused("No such phase", $"'{request.Phase}' is not an alert {name}'s script causes: {string.Join(", ", entry.Phases.Select(p => p.Alert))}.");
            }

            if (phase.ConfirmWith is { } expected && request.Confirm != expected)
            {
                return Refused("Confirmation required", $"Causing {phase.Alert} costs the stack ({phase.Impact}). Send confirm: \"{expected}\".");
            }

            return gameDay.Start(entry.Runbook, GameDayMode.Cause, request.Phase, phase.Alert)
                .Answer(GameDayService.Kind(entry.Runbook, GameDayMode.Cause, phase.Alert));
        });

        // The undo deletes nothing the cause did not make and is safe twice, so it takes no confirmation.
        app.MapPost("/api/runbooks/{name}/restore", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (
            string name, GameDayService gameDay, CancellationToken cancellationToken) =>
        {
            (RunbookEntry? entry, ProblemHttpResult? refused) = await ScriptedAsync(name, gameDay, cancellationToken);

            return entry is null
                ? refused!
                : gameDay.Start(entry.Runbook, GameDayMode.Restore, null).Answer(GameDayService.Kind(entry.Runbook, GameDayMode.Restore));
        });

        return app;
    }

    /// <summary>The listed runbook a script causes, or the refusal for a name that is not one: the tool's own catalogue decides, never the request.</summary>
    private static async Task<(RunbookEntry? Entry, ProblemHttpResult? Refusal)> ScriptedAsync(
        string name, GameDayService gameDay, CancellationToken cancellationToken)
    {
        if (!GameDayService.IsRunbookName(name))
        {
            return (null, Refused("Not a runbook name", $"'{name}' is not the name of a file in docs/runbooks/."));
        }

        RunbookCatalogue catalogue = await gameDay.ListAsync(cancellationToken);

        if (catalogue.Error is { } error)
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The game day could not be listed", detail: error));
        }

        RunbookEntry? entry = catalogue.Runbooks.FirstOrDefault(r => r.Runbook == name);

        if (entry is null)
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such runbook", detail: $"The game day lists no runbook '{name}'."));
        }

        return entry.Script
            ? (entry, null)
            : (null, Refused("No script", $"{name} has no script: {entry.Reason}"));
    }

    private static ProblemHttpResult Refused(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);
}
