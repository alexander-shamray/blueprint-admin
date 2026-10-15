using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Broker;

namespace Admin.Host.Fakes;

/// <summary>
/// What blueprint-backend's <c>tools/dead-letters</c> prints for the two parked messages <c>rabbitmq-queues.json</c>
/// holds, one on <c>ordering-catalog-events_error</c> and one on <c>bff-order-events_skipped</c>, for
/// <see cref="FakePlatformScripts"/> to replay. Written by hand from the tool's <c>emit</c>, <c>describe</c> and
/// <c>finish</c>, not recorded: a stack on the backend that ships the tool had no parked message to read, and a
/// recording would hold whatever a fault happened to park. The ids and the correlation id are the fake platform's own.
/// </summary>
internal static class DeadLetterRecordings
{
    public const string FaultedId = "0199b2a0-5c1d-7e2f-8a3b-4c5d6e7f8a90";

    public const string SkippedId = "0199b2a1-1e2f-7a3b-9c4d-5e6f7a8b9c0d";

    /// <summary>The prefix of every tool line, after <see cref="DeadLetterService.Python"/>'s first word.</summary>
    public static string Prefix => $"{string.Join(' ', DeadLetterService.Python[1..])} {DeadLetterService.Tool} ";

    private static readonly Dictionary<string, JsonObject> Messages = new(StringComparer.Ordinal)
    {
        ["ordering-catalog-events_error"] = Message(
            FaultedId,
            "fake-corr-0002",
            ["urn:message:Common.Contracts.Catalog.V1:PriceChanged"],
            "Product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f has no row in the price projection",
            "System.InvalidOperationException",
            "rabbitmq://rabbitmq/ordering-catalog-events",
            "fault"),
        ["bff-order-events_skipped"] = Message(
            SkippedId,
            "fake-corr-0003",
            ["urn:message:Common.Contracts.Ordering.V1:OrderShipped"],
            null,
            null,
            null,
            "skip"),
    };

    /// <summary><c>inspect QUEUE --limit N --json</c>, indented as the tool prints it: the one message it holds, or none.</summary>
    public static IEnumerable<string> Inspect(IReadOnlyList<string> rest)
    {
        string queue = rest[0];
        JsonObject document = new()
        {
            ["schema"] = 1,
            ["command"] = "inspect",
            ["vhost"] = "/",
            ["queue"] = queue,
            ["kind"] = ParkedQueues.KindOf(queue)?.Name,
            ["endpoint"] = ParkedQueues.EndpointOf(queue),
            ["messages"] = new JsonArray(Messages.TryGetValue(queue, out JsonObject? message) ? [message.DeepClone()] : []),
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Split('\n');
    }

    /// <summary>
    /// <c>replay</c> or <c>discard</c> of the named messages, as <c>finish</c> words each action: a dry run's
    /// <c>would-</c> lines and its closing line, or the real run's past tense. An id the queue does not hold is not found.
    /// </summary>
    public static IEnumerable<string> Move(IReadOnlyList<string> rest)
    {
        string verb = rest[0];
        string queue = rest[1];
        bool execute = rest.Contains(DeadLetterService.ExecuteConfirm);
        string held = Messages.TryGetValue(queue, out JsonObject? message) ? (string)message["message_id"]! : "";
        string? destination = verb == "replay" ? $" -> {ParkedQueues.EndpointOf(queue)}" : "";
        IEnumerable<string> ids = rest.Select((word, at) => (word, at)).Where(w => w.at > 0 && rest[w.at - 1] == "--message-id").Select(w => w.word);

        foreach (string id in ids)
        {
            yield return id == held
                ? $"{(execute ? $"{verb}ed" : $"would-{verb}"),-14} {id}{destination}"
                : $"{"not found",-14} {id}";
        }

        if (!execute)
        {
            yield return $"dry run: nothing left {queue}. Pass --execute --record FILE to {verb}; the read requeued what it saw, flagged redelivered";
        }
    }

    private static JsonObject Message(
        string id, string correlationId, string[] messageType, string? faultMessage, string? faultType, string? inputAddress, string reason)
    {
        JsonObject envelope = new()
        {
            ["messageId"] = id,
            ["correlationId"] = correlationId,
            ["messageType"] = new JsonArray([.. messageType.Select(t => (JsonNode)t)]),
            ["message"] = new JsonObject { ["productId"] = "0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f" },
        };
        string body = envelope.ToJsonString();

        return new JsonObject
        {
            ["message_id"] = id,
            ["correlation_id"] = correlationId,
            ["message_type"] = new JsonArray([.. messageType.Select(t => (JsonNode)t)]),
            ["fault_message"] = faultMessage,
            ["fault_exception_type"] = faultType,
            ["input_address"] = inputAddress,
            ["reason"] = reason,
            ["redelivered"] = true,
            ["properties"] = new JsonObject { ["message_id"] = id, ["correlation_id"] = correlationId, ["content_type"] = "application/vnd.masstransit+json" },
            ["body"] = body,
            ["body_base64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body)),
        };
    }
}
