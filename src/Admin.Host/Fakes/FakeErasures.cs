using System.Globalization;
using System.Text.Json;
using Admin.Host.Privacy;

namespace Admin.Host.Fakes;

/// <summary>
/// The erasure requests FakePlatform has raised, each answered by its holders as the real ones answer: a few on the
/// first read, the rest on the next. Written by hand rather than recorded, because a recording holds one instant of a
/// request and these have to move. The bodies are blueprint-backend's <c>ErasureRequestView</c> and <c>HolderAnswer</c>
/// (<c>src/Services/Privacy/Privacy.Application/ErasureRequests/GetErasureRequest/ErasureRequestView.cs</c>); the
/// cadence is the fake's own.
/// </summary>
/// <remarks>
/// The subject <see cref="OverdueSubject"/> is the one that never finishes by itself: Notifications and the BFF do not
/// answer, the request reads <c>Overdue</c>, and only a reissue makes them answer, as a runbook's reissue does once the
/// holder is back. Every other subject closes on its second read. Asking again for a subject whose request is open
/// returns that request (<c>RaiseErasureRequestCommand</c>, ADR-092).
/// </remarks>
public sealed class FakeErasures(TimeProvider time)
{
    /// <summary>The subject whose request goes overdue, so the Erasure screen has a row to reissue.</summary>
    public static readonly Guid OverdueSubject = Guid.Parse("00000000-0000-0000-0000-00000000dead");

    /// <summary>Holders that answer the first read; the rest answer the second, or never for <see cref="OverdueSubject"/> until a reissue.</summary>
    private const int AnsweringFirst = 3;

    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Request> requests = [];
    private long next = 0x300;

    /// <summary>The id of the request now open for the subject: the one already open, or a new one.</summary>
    public Guid Raise(Guid subject)
    {
        lock (gate)
        {
            foreach (KeyValuePair<Guid, Request> open in requests)
            {
                if (open.Value.Subject == subject && !open.Value.IsClosed)
                {
                    return open.Key;
                }
            }

            Guid id = Guid.Parse("0199a1b2-0000-7000-8000-" + (next++).ToString("x12", CultureInfo.InvariantCulture));
            DateTimeOffset now = time.GetUtcNow();
            requests[id] = new Request(subject, now, subject == OverdueSubject ? now : now + Slo);

            return id;
        }
    }

    /// <summary>The request as the status route shows it, or null for an id the fake never raised. Each read moves the holders on.</summary>
    public string? Read(Guid id)
    {
        lock (gate)
        {
            if (!requests.TryGetValue(id, out Request? request))
            {
                return null;
            }

            request.Reads++;
            string[] holders = [.. ErasureHolders.Table.Select(h => h.Name)];
            bool stuck = request.Subject == OverdueSubject && request.Reissues == 0;
            int answered = request.Reads == 1 ? AnsweringFirst : stuck ? AnsweringFirst : holders.Length;

            if (answered == holders.Length && request.ClosedAt is null)
            {
                request.ClosedAt = time.GetUtcNow();
            }

            string status = request.ClosedAt is not null ? "Closed" : request.Subject == OverdueSubject ? "Overdue" : "Open";

            return JsonSerializer.Serialize(new
            {
                requestId = id,
                status,
                raisedAt = request.RaisedAt,
                dueAt = request.DueAt,
                closedAt = request.ClosedAt,
                reissues = request.Reissues,
                responders = holders,
                missing = holders.Skip(answered).ToArray(),
                answers = holders.Take(answered).Select(h => new { responder = h, count = 1, counted = true, receivedAt = request.RaisedAt.AddSeconds(1) }).ToArray(),
            }, JsonSerializerOptions.Web);
        }
    }

    /// <summary>Asks the holders again: <see cref="Reissued.NotFound"/> for an id never raised, <see cref="Reissued.Closed"/> once every holder has answered.</summary>
    public Reissued Reissue(Guid id)
    {
        lock (gate)
        {
            if (!requests.TryGetValue(id, out Request? request))
            {
                return Reissued.NotFound;
            }

            if (request.IsClosed)
            {
                return Reissued.Closed;
            }

            request.Reissues++;

            return Reissued.Asked;
        }
    }

    private sealed class Request(Guid subject, DateTimeOffset raisedAt, DateTimeOffset dueAt)
    {
        public Guid Subject { get; } = subject;

        public DateTimeOffset RaisedAt { get; } = raisedAt;

        public DateTimeOffset DueAt { get; } = dueAt;

        public DateTimeOffset? ClosedAt { get; set; }

        public int Reads { get; set; }

        public int Reissues { get; set; }

        public bool IsClosed => ClosedAt is not null;
    }
}

public enum Reissued
{
    Asked,
    NotFound,
    Closed,
}
