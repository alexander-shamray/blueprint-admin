namespace Admin.Host.Mail;

/// <summary>A mailbox as the sink parsed it: the display name, empty when there is none, and the address.</summary>
public sealed record Mailbox(string Name, string Address);

/// <summary>
/// One message in the sink's list. <see cref="CorrelationId"/> is the message's <c>X-Correlation-Id</c> header when it
/// carries one the backend would adopt, and null otherwise: a value the trace endpoint would refuse is no link.
/// <see cref="MessageId"/> is the message's <c>Message-ID</c> as the sink lists it, <c>{event id}.{template key}@{domain}</c>
/// (blueprint-backend <c>MailMessageId</c>), which says which event the message answered.
/// </summary>
public sealed record MailSummary(
    string Id,
    string MessageId,
    Mailbox From,
    IReadOnlyList<Mailbox> To,
    string Subject,
    DateTimeOffset Created,
    string Snippet,
    bool Read,
    string? CorrelationId);

/// <summary>
/// The Mail screen's list, newest first. A sink that does not answer is a state, not an exception (spec §9):
/// <see cref="Reachable"/> is false and <see cref="Error"/> says why. <see cref="Total"/> is what the sink holds,
/// which may be more than the <see cref="MailSinkClient.Limit"/> listed. <see cref="SinkUrl"/> is the sink's own UI,
/// where clearing it is done.
/// </summary>
public sealed record MailListView(bool Reachable, string? Error, int Total, IReadOnlyList<MailSummary> Messages, string SinkUrl);

/// <summary>One message opened: the rendered parts as the sink split them, either of which may be empty.</summary>
public sealed record MailMessageView(
    string Id,
    Mailbox From,
    IReadOnlyList<Mailbox> To,
    IReadOnlyList<Mailbox> Cc,
    string Subject,
    DateTimeOffset Date,
    string Text,
    string Html,
    string? CorrelationId);

/// <summary>A message read: the message, or why there is none — the sink down, or its own answer, 404 included.</summary>
public sealed record MailMessageRead(bool Reachable, string? Error, MailMessageView? Message);
