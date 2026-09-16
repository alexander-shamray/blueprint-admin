using Microsoft.AspNetCore.Http.HttpResults;
using CorrelationId = Admin.Host.Api.CorrelationId;

namespace Admin.Host.Trace;

/// <summary>
/// One correlation id's timeline (spec §5.10). The id is validated before it reaches the service
/// because it is interpolated into a LogQL query there: the backend's alphabet holds no quote,
/// brace, backslash or pipe, so a validated id cannot close the string literal or add a stage.
/// </summary>
public static class TraceEndpoints
{
    public static IEndpointRouteBuilder MapTrace(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/trace/{correlationId}", async Task<Results<Ok<TraceView>, ProblemHttpResult>> (
            string correlationId,
            string? window,
            EventTraceService trace,
            CancellationToken cancellationToken) =>
        {
            if (!CorrelationId.IsAdoptable(correlationId))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid correlation id",
                    detail: $"A correlation id is 1-{CorrelationId.MaxLength} characters of letters, digits, '-' or '_'.");
            }

            if (!TraceWindow.TryParse(window, out TimeSpan parsed, out string? error))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid window",
                    detail: error);
            }

            return TypedResults.Ok(await trace.BuildAsync(correlationId, parsed, cancellationToken));
        });

        return app;
    }
}
