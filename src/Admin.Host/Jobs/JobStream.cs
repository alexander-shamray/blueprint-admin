using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Admin.Host.Jobs;

/// <summary>
/// One job as Server-Sent Events: a <c>line</c> event per output line whose
/// id is the sequence number, so a reconnecting EventSource resumes from the
/// ring buffer, then one <c>exited</c> event. A reader that fell too far
/// behind gets the end of the response without <c>exited</c>: the browser
/// reconnects with Last-Event-ID and the ring supplies what it missed.
/// </summary>
public static class JobStream
{
    // One shared instance: OutputLine here must serialize the same way it
    // does from GET /api/jobs/{id} (JsonStringEnumConverter, web defaults),
    // and building a fresh JsonSerializerOptions per event is wasted work.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async IAsyncEnumerable<SseItem<string>> Events(Job job, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long last = afterSequence;

        await foreach (OutputLine line in job.Follow(afterSequence, cancellationToken))
        {
            last = line.Sequence;

            yield return new SseItem<string>(JsonSerializer.Serialize(line, Options), "line")
            {
                EventId = line.Sequence.ToString(CultureInfo.InvariantCulture),
            };
        }

        // The follow ended with lines still undelivered, or with the job still
        // running: the follower overflowed. Ending here makes the browser resume.
        if (job.State != JobState.Exited || job.HasLinesAfter(last))
        {
            yield break;
        }

        int exitCode = await job.Completion.WaitAsync(cancellationToken);

        yield return new SseItem<string>(JsonSerializer.Serialize(new { exitCode }, Options), "exited");
    }
}
