namespace Admin.Host.Broker;

/// <summary>What a move does to the messages it names: the two verbs of blueprint-backend's <c>tools/dead-letters</c>.</summary>
public enum DeadLetterVerb
{
    /// <summary>Back to the endpoint the queue name gives.</summary>
    Replay,

    /// <summary>Removed, once each is in the record file.</summary>
    Discard,
}

/// <summary>
/// One parked message as <c>inspect --json</c> describes it (the tool's README, "The JSON contract", schema 1).
/// <see cref="MessageType"/> is the body's <c>messageType</c>, joined when MassTransit gives several; <see cref="Body"/>
/// is null when the body is not UTF-8.
/// </summary>
public sealed record DeadLetterMessage(
    string? MessageId,
    string? CorrelationId,
    string? MessageType,
    string? FaultMessage,
    string? FaultExceptionType,
    string? InputAddress,
    string? Reason,
    bool Redelivered,
    string? Body);

/// <summary>
/// A parked queue's messages, read and put back. A tool or broker that does not answer is a state, not an exception
/// (spec §9): <see cref="Error"/> says why, and <see cref="Messages"/> is empty.
/// </summary>
public sealed record DeadLetterInspection(string Queue, string? Endpoint, string? Kind, IReadOnlyList<DeadLetterMessage> Messages, string? Error);

/// <summary>A replay or discard as the Broker screen sends it: the messages by id, and whether it is the real run.</summary>
public sealed record DeadLetterMoveRequest(IReadOnlyList<string>? MessageIds, bool Execute, string? Confirm);

/// <summary>What the screen states before any button is pressed (<c>GET /api/broker/dead-letters</c>).</summary>
public sealed record DeadLetterPlan(string ExecuteConfirm, int InspectLimit, string RecordDir);
