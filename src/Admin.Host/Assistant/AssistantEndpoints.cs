using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant;
using Common.Assistant.Protocol;
using Common.Assistant.Tools;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Assistant;

/// <summary>
/// <c>/api/assistant</c> (AI assistant design §5.1), under spec §8's rules: loopback only, and <c>LoopbackOriginGuard</c>
/// refuses a cross-site request before any of this runs. With the assistant off only the status route is mapped, so
/// every other <c>/api/assistant/*</c> path is the catch-all's 404 and nothing can open a conversation (§9.1).
/// <c>POST …/confirmations</c> is reserved (§6.3) and stays that 404 until actions are allowed. <c>/api/assistant/tools</c> is
/// the MCP route's (§12): the server tools outside any conversation, which <c>src/Admin.Mcp</c> reads.
/// </summary>
public static class AssistantEndpoints
{
    /// <summary>The longest message a caller may send, in characters.</summary>
    public const int MaxTextLength = 16_000;

    /// <summary>
    /// The title of the 404 for a tool the host does not have. <c>HttpAdminHost</c> in <c>src/Admin.Mcp</c> copies it to
    /// tell this answer from the catch-all's 404 of an assistant that is off; a test holds the two together.
    /// </summary>
    public const string NoSuchTool = "No such tool";

    /// <summary>The most catalogue entries a client may send.</summary>
    public const int MaxCatalogue = 500;

    // The events serialize as the rest of /api answers, so the TypeScript mirror reads one shape.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web);

    public static WebApplication MapAssistant(this WebApplication app)
    {
        AssistantRuntime runtime = app.Services.GetRequiredService<AssistantRuntime>();

        app.MapGet("/api/assistant", async (CancellationToken cancellationToken) =>
            runtime.Core is { } on ? await on.StatusAsync(cancellationToken) : AssistantStatus.Off);

        if (runtime.Core is not AssistantCore core || runtime.ServerTools is not ServerToolCalls serverTools)
        {
            return app;
        }

        Caller caller = AssistantRuntime.Operator;

        // The MCP route's tools (§12): the host's server tools, never a client's, through the registry, policy, limits and
        // redactor a conversation's calls go through. The platform tools are listed from the API catalogue, as for a conversation.
        // The registry is rebuilt on each call, because the platform tools are listed from a catalogue that changes. A source
        // whose tool the policy then refuses is the host's fault, answered as a problem as a conversation's open is (400).
        app.MapGet("/api/assistant/tools", async Task<Results<Ok<IReadOnlyList<ToolDescriptor>>, ProblemHttpResult>> (CancellationToken cancellationToken) =>
        {
            await runtime.PrepareAsync(cancellationToken);

            try
            {
                return TypedResults.Ok(serverTools.Tools());
            }
            catch (ToolRefusedException refused)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "A tool was refused", detail: refused.Message);
            }
        });

        app.MapPost("/api/assistant/tools/{name}", async Task<Results<Ok<ToolOutcome>, ProblemHttpResult>> (string name, JsonObject? arguments, CancellationToken cancellationToken) =>
        {
            await runtime.PrepareAsync(cancellationToken);

            try
            {
                return await serverTools.CallAsync(caller, name, arguments ?? [], cancellationToken) is { } outcome
                    ? TypedResults.Ok(outcome)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: NoSuchTool, detail: name);
            }
            catch (ToolRefusedException refused)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "A tool was refused", detail: refused.Message);
            }
        });

        app.MapPost("/api/assistant/conversations", async Task<Results<Ok<ConversationOpened>, ProblemHttpResult>> (OpenConversation? request, CancellationToken cancellationToken) =>
        {
            if (Invalid(request) is { } problem)
            {
                return problem;
            }

            // The platform tools are listed from the API catalogue, which a conversation's tools are fixed from (§6.5).
            await runtime.PrepareAsync(cancellationToken);

            try
            {
                return core.Open(caller, request!) is { } session
                    ? TypedResults.Ok(new ConversationOpened(session.Id))
                    : TypedResults.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many conversations",
                        detail: "Every conversation this console holds is running a turn; let one end or cancel it.");
            }
            catch (ToolRefusedException refused)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A client tool was refused", detail: refused.Message);
            }
        });

        app.MapGet("/api/assistant/conversations/{id}", Results<Ok<ConversationView>, ProblemHttpResult> (string id) =>
            core.Find(caller, id) is { } session ? TypedResults.Ok(session.View()) : Unknown(id));

        app.MapPost("/api/assistant/conversations/{id}/turns", Results<Accepted, ProblemHttpResult> (string id, StartTurn? request) =>
        {
            if (core.Find(caller, id) is not { } session)
            {
                return Unknown(id);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxTextLength)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Not a message",
                    detail: $"A turn needs text, at most {MaxTextLength} characters.");
            }

            return session.TryStart(request)
                ? TypedResults.Accepted((string?)null)
                : TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "A turn is running",
                    detail: "Wait for the running turn to end, or cancel it.");
        });

        app.MapGet("/api/assistant/conversations/{id}/events", async Task (string id, long? after, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (core.Find(caller, id) is not { } session)
            {
                await Unknown(id).ExecuteAsync(context);

                return;
            }

            // The header wins, as for a job's stream: EventSource reconnects to the URL it was opened with.
            long resumeAfter = ParseLastEventId(context) ?? after ?? 0;

            // An idle conversation has no event to send, and a response's headers wait for its first write, so the
            // stream is started at once: a client that waits for it to open before it sends a turn is not left waiting.
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache,no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.ContentEncoding = "identity";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            await SseFormatter.WriteAsync(Events(session, resumeAfter, cancellationToken), context.Response.Body, cancellationToken);
        });

        app.MapPost("/api/assistant/conversations/{id}/tool-results", Results<NoContent, ProblemHttpResult> (string id, ClientToolResult? result) =>
        {
            if (core.Find(caller, id) is not { } session)
            {
                return Unknown(id);
            }

            if (result is null || result.CallId is null || result.Content is null)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a tool result", detail: "Send callId, ok and content.");
            }

            return session.TryAnswer(result)
                ? TypedResults.NoContent()
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such tool call", detail: result.CallId);
        });

        app.MapPost("/api/assistant/conversations/{id}/cancel", Results<NoContent, ProblemHttpResult> (string id) =>
        {
            if (core.Find(caller, id) is not { } session)
            {
                return Unknown(id);
            }

            session.Cancel();

            return TypedResults.NoContent();
        });

        app.MapDelete("/api/assistant/conversations/{id}", Results<NoContent, ProblemHttpResult> (string id) =>
            core.Delete(caller, id) ? TypedResults.NoContent() : Unknown(id));

        return app;
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(AssistantSession session, long afterId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach ((long id, AssistantEvent value) in session.Events(afterId, cancellationToken))
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(value, value.GetType(), Options), value.EventName)
            {
                EventId = id.ToString(CultureInfo.InvariantCulture),
            };
        }
    }

    private static ProblemHttpResult? Invalid(OpenConversation? request)
    {
        string? why = request switch
        {
            null => "The body is not a conversation to open.",
            { Client: null } => "Send client, its name and version.",
            { Locale: null or "" } => "Send locale, a BCP 47 tag such as en-US.",
            { Catalogue: null } => "Send catalogue, the entries the assistant explains.",
            { Catalogue.Count: > MaxCatalogue } => $"A catalogue holds at most {MaxCatalogue} entries.",
            { ClientTools: null } => "Send clientTools, empty when the client has none.",
            _ when request.Catalogue.Any(e => e is null || e.Id is null || e.Label is null || e.Help is null || e.Where is null) =>
                "Every catalogue entry needs id, label, where and help.",
            _ when request.ClientTools.Any(t => t is null || t.Name is null || t.Title is null || t.Description is null || t.InputSchema is null || t.Annotations is null) =>
                "Every client tool needs name, title, description, inputSchema and annotations.",
            _ => null,
        };

        return why is null ? null : TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a conversation", detail: why);
    }

    // Another caller's id answers the same as an unknown one, so an id reveals nothing (§5.2).
    private static ProblemHttpResult Unknown(string id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation", detail: id);

    private static long? ParseLastEventId(HttpContext context)
    {
        string? header = context.Request.Headers["Last-Event-ID"].FirstOrDefault();

        return long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }
}
