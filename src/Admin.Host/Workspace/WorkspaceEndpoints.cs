using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Workspace;

/// <summary>
/// <c>/api/workspace</c> (widget-workspace spec §6.2, host spec §5.10). The writes are <c>/api</c> writes, so
/// <c>LoopbackOriginGuard</c> refuses a cross-site one before any of this runs (§9).
/// </summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspace(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspace", (HttpContext http, WorkspaceStore store) =>
            Guarded(() => Document(http, StatusCodes.Status200OK, store.Read())));

        app.MapPut("/api/workspace", async (HttpContext http, WorkspaceStore store, CancellationToken cancellationToken) =>
        {
            string ifMatch = http.Request.Headers.IfMatch.ToString();

            if (ifMatch.Length == 0)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status428PreconditionRequired,
                    title: "If-Match required",
                    detail: "Send the etag of the workspace this one is based on as If-Match; GET /api/workspace has it.");
            }

            (JsonNode? body, IResult? refusal) = await ReadBody(http.Request, cancellationToken);

            return refusal ?? Guarded(() => Answer(http, store.Save(body, ifMatch)));
        });

        app.MapGet("/api/workspace/export", (WorkspaceStore store) =>
            Guarded(() => Results.File(
                System.Text.Encoding.UTF8.GetBytes(store.Read().Json),
                "application/json",
                "blueprint-admin-workspace.json")));

        app.MapPost("/api/workspace/import", async (HttpContext http, WorkspaceStore store, CancellationToken cancellationToken) =>
        {
            (JsonNode? body, IResult? refusal) = await ReadBody(http.Request, cancellationToken);

            return refusal ?? Guarded(() => Answer(http, store.Replace(body)));
        });

        return app;
    }

    private static IResult Answer(HttpContext http, SaveResult result) => result.Outcome switch
    {
        SaveOutcome.Saved => Document(http, StatusCodes.Status200OK, result.Snapshot!),
        SaveOutcome.Stale => Document(http, StatusCodes.Status412PreconditionFailed, result.Snapshot!),
        _ => TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Not a workspace",
            detail: result.Problem),
    };

    private static IResult Document(HttpContext http, int status, WorkspaceSnapshot snapshot)
    {
        http.Response.Headers.ETag = snapshot.Etag;

        return Results.Text(snapshot.Json, "application/json", statusCode: status);
    }

    // A file that cannot be read or written is the operator's disk, not a bug: say which, with the status the screens already show.
    private static IResult Guarded(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The workspace file could not be read or written",
                detail: e.Message);
        }
    }

    private static async Task<(JsonNode? Body, IResult? Refusal)> ReadBody(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > WorkspaceSchema.MaxDocumentBytes)
        {
            return (null, TooLarge());
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > WorkspaceSchema.MaxDocumentBytes)
            {
                return (null, TooLarge());
            }
        }

        buffer.Position = 0;

        try
        {
            return (JsonNode.Parse(buffer), null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return (null, TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a workspace",
                detail: $"The body is not JSON: {e.Message}"));
        }
    }

    private static ProblemHttpResult TooLarge() => TypedResults.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Workspace too large",
        detail: $"A workspace is at most {WorkspaceSchema.MaxDocumentBytes / (1024 * 1024)} MiB.");
}
