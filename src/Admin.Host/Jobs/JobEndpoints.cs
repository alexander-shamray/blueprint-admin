using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Jobs;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", (JobRegistry registry) => TypedResults.Ok(registry.All().Select(JobSummary.Of).ToList()));

        app.MapGet("/api/jobs/{id}", Results<Ok<JobView>, ProblemHttpResult> (string id, JobRegistry registry) =>
            registry.Find(id) is Job job
                ? TypedResults.Ok(new JobView(JobSummary.Of(job), job.Tail(200)))
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job", detail: id));

        app.MapGet("/api/jobs/{id}/stream", IResult (string id, long? after, HttpContext context, JobRegistry registry, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is not Job job)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job", detail: id);
            }

            // The header wins: EventSource reconnects to the URL it was opened with, so
            // an explicit ?after is stale once the browser has sent Last-Event-ID (spec §5.10).
            long resumeAfter = ParseLastEventId(context) ?? after ?? -1;

            return TypedResults.ServerSentEvents(JobStream.Events(job, resumeAfter, cancellationToken));
        });

        return app;
    }

    private static long? ParseLastEventId(HttpContext context)
    {
        string? header = context.Request.Headers["Last-Event-ID"].FirstOrDefault();

        return long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }
}

public sealed record JobView(JobSummary Summary, IReadOnlyList<OutputLine> Lines);
