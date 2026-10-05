namespace Admin.Host.Transcript;

public static class TranscriptEndpoints
{
    public static IEndpointRouteBuilder MapTranscript(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transcript", (OperatorTranscript transcript) => TypedResults.Ok(transcript.Read()));

        app.MapGet("/api/transcript/script", (OperatorTranscript transcript) =>
            TypedResults.Text(transcript.Script(), "text/plain; charset=utf-8"));

        return app;
    }
}
