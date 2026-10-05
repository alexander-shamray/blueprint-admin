using System.Globalization;
using System.Text.Json;

namespace Admin.Host.Fakes;

/// <summary>
/// The orders FakePlatform has placed, each advancing as the saga and Shipping would advance it, so the Scenario's
/// watch steps have something to watch. Written by hand rather than recorded: a recording holds one instant of an
/// order, and these have to move. The bodies are blueprint-backend's <c>OrderDetail</c> and <c>OrderSummary</c>
/// (<c>src/BFF/Web.Bff/Orders/OrderResponses.cs</c>), the status chosen as <c>BuyerStatus.Of</c> chooses it, and
/// the cadence is the fake's own: one <see cref="Step"/> from the place to confirmed, to dispatched, to delivered. A
/// cancel takes effect one step after it is asked for, unless the order is dispatched first.
/// </summary>
/// <remarks>
/// The first order is <see cref="FakeGateway.PlacedOrderId"/>, the id the reservation and payment recordings carry;
/// each later one gets an id of its own, so two runs at once each watch their own order.
/// </remarks>
public sealed class FakeOrders(TimeProvider time)
{
    internal static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>Later ids count up from here, clear of the fakes' fixed product and order ids.</summary>
    private const long FirstLaterId = 0x100;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Placed> placed = new(StringComparer.OrdinalIgnoreCase);
    private long next = FirstLaterId;

    /// <summary>Places an order and answers with its id.</summary>
    public string Place()
    {
        lock (gate)
        {
            string id = placed.Count == 0
                ? FakeGateway.PlacedOrderId
                : "0199a1b2-0000-7000-8000-" + (next++).ToString("x12", CultureInfo.InvariantCulture);
            placed[id] = new Placed(time.GetUtcNow(), null);
            return id;
        }
    }

    /// <summary>A cancel for an order never placed changes nothing; the fake's cancel answers 204 for any id, as before.</summary>
    public void Cancel(string orderId)
    {
        lock (gate)
        {
            if (placed.TryGetValue(orderId, out Placed? order) && order.CancelAskedAt is null)
            {
                placed[orderId] = order with { CancelAskedAt = time.GetUtcNow() };
            }
        }
    }

    /// <summary>The BFF's detail for <paramref name="orderId"/>, or null where it answers 404: an order it never learned of.</summary>
    public string? Detail(string orderId)
    {
        Placed? order;

        lock (gate)
        {
            placed.TryGetValue(orderId, out order);
        }

        return order is null ? null : JsonSerializer.Serialize(View(orderId, order, detail: true));
    }

    /// <summary>The BFF's first page of the buyer's orders, newest first.</summary>
    public string Page()
    {
        KeyValuePair<string, Placed>[] orders;

        lock (gate)
        {
            orders = [.. placed.OrderByDescending(o => o.Value.At)];
        }

        return JsonSerializer.Serialize(new { items = orders.Select(o => View(o.Key, o.Value, detail: false)), nextCursor = (string?)null });
    }

    /// <summary><c>OrderDetail</c> when <paramref name="detail"/>, else the <c>OrderSummary</c> the list carries.</summary>
    private object View(string orderId, Placed order, bool detail)
    {
        DateTimeOffset p = order.At;
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset? cancelled = order.CancelAskedAt is { } c && c + Step < p + (2 * Step) && c + Step <= now ? c + Step : null;
        DateTimeOffset? confirmed = Reached(p + Step, now, cancelled);
        DateTimeOffset? dispatched = Reached(p + (2 * Step), now, cancelled);
        DateTimeOffset? delivered = Reached(p + (3 * Step), now, cancelled);

        string status = delivered is not null ? "delivered"
            : cancelled is not null ? "cancelled"
            : dispatched is not null ? "dispatched"
            : confirmed is not null ? "confirmed"
            : "placed";

        DateTimeOffset asOf = new[] { p, confirmed, dispatched, delivered, cancelled }.Max()!.Value;
        var money = new { amount = 19.99m, currency = "EUR" };
        var timeline = new { placed = p, confirmed, dispatched, delivered, cancelled };
        bool cancellable = status is "placed" or "confirmed";

        if (!detail)
        {
            return new
            {
                orderId,
                status,
                timeline,
                refunded = false,
                refundedAt = (DateTimeOffset?)null,
                cancellable,
                total = money,
                lines = new[] { new { productId = FakeGateway.StockedProductId, productName = "Walnut desk", lineTotal = money } },
                asOf,
            };
        }

        return new
        {
            orderId,
            status,
            timeline,
            refunded = false,
            refundedAt = (DateTimeOffset?)null,
            cancellable,
            total = money,
            lines = new[]
            {
                new { productId = FakeGateway.StockedProductId, productName = "Walnut desk", lineTotal = money, quantity = 1, unitPrice = money },
            },
            asOf,
            // The saga authorises before it confirms; the fake reports both at once.
            payment = confirmed is null ? null : new { authorisedAt = confirmed, amount = money, refundedAmount = (object?)null },
            shipment = dispatched is null ? null : new { trackingNumber = (string?)null, dispatchedAt = dispatched, deliveredAt = delivered },
        };
    }

    /// <summary>A step happens at its time unless the order was cancelled first.</summary>
    private static DateTimeOffset? Reached(DateTimeOffset at, DateTimeOffset now, DateTimeOffset? cancelled) =>
        at <= now && (cancelled is null || at < cancelled) ? at : null;

    private sealed record Placed(DateTimeOffset At, DateTimeOffset? CancelAskedAt);
}
