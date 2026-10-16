using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Mail;

/// <summary>
/// The Mail screen's reads (spec §5.10). Both answer 200 whatever the sink did, its own 404 included, since a sink
/// that is down is a state (spec §9); only an id that is not one of the sink's is the host's 400, because it becomes a
/// path segment of the URL the host builds.
/// </summary>
public static class MailEndpoints
{
    public static IEndpointRouteBuilder MapMail(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/mail/messages", async (MailSinkClient sink, CancellationToken cancellationToken) =>
            TypedResults.Ok(await sink.ListAsync(cancellationToken)));

        app.MapGet("/api/mail/messages/{id}", async Task<Results<Ok<MailMessageRead>, ProblemHttpResult>> (
            string id,
            MailSinkClient sink,
            CancellationToken cancellationToken) =>
        {
            if (!MailSinkClient.IsMessageId(id))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid message id",
                    detail: "A mail sink message id is 1-64 letters or digits.");
            }

            return TypedResults.Ok(await sink.ReadAsync(id, cancellationToken));
        });

        return app;
    }
}
