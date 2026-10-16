using Admin.Host.Fakes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Scenario;

/// <summary>The Markdown a finished Scenario run was written up as, before it is scrubbed (spec §5.13).</summary>
public sealed record ScenarioExportRequest(string? Markdown);

/// <summary>
/// The scrubber's last word on a Scenario export (spec §5.13). The page writes the document, because it holds the run; the
/// scrubber lives here, so the page sends the text back and downloads what comes out, and no bytes of the export reach a
/// file the scrubber has not read. Nothing is stored: the text is answered and forgotten, as the transcript's script is rendered.
/// </summary>
public static class ExportEndpoints
{
    /// <summary>The most characters one export may carry; a run's document is tens of kilobytes, and the scrubber's patterns are not free.</summary>
    public const int MaxCharacters = 2_000_000;

    public static IEndpointRouteBuilder MapScenarioExport(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scenario/export", Results<ContentHttpResult, ProblemHttpResult> (ScenarioExportRequest request) =>
        {
            if (string.IsNullOrEmpty(request.Markdown))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Nothing to export",
                    detail: "The request carries no document.");
            }

            if (request.Markdown.Length > MaxCharacters)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Export too large",
                    detail: $"A document is at most {MaxCharacters} characters.");
            }

            return TypedResults.Text(FixtureScrubber.Scrub(request.Markdown), "text/markdown; charset=utf-8");
        });

        return app;
    }
}
