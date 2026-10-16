using Admin.Host.Trace;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Scenario;

/// <summary>
/// The review flag an order has in Loki (spec §5.3). The id is parsed as a Guid before it reaches the service because it is
/// interpolated into a LogQL query there, and the window is the trace screen's, bounded for the same reason.
/// </summary>
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/review/{orderId}", async Task<Results<Ok<ReviewFlagView>, ProblemHttpResult>> (
            string orderId,
            string? window,
            ReviewFlagService review,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(orderId, out Guid id))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid order id",
                    detail: "An order id is a Guid.");
            }

            if (!TraceWindow.TryParse(string.IsNullOrWhiteSpace(window) ? ReviewFlagService.DefaultWindow : window, out TimeSpan parsed, out string? error))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid window",
                    detail: error);
            }

            return TypedResults.Ok(await review.ReadAsync(id, parsed, cancellationToken));
        });

        return app;
    }
}
