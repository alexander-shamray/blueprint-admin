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
/// <para>
/// Scripted state (docs/superpowers/specs/2026-10-15-scenario-scripts.md): the fake remembers what each publish priced and what each stock write set,
/// and an order for such a product ends as the platform ends it when run-locally.md's cause is applied by hand. Stock
/// set below one ends it <c>out_of_stock</c>; a price whose minor units are <see cref="DeclinedMinorUnits"/> ends it
/// <c>declined</c> (the payment simulator's <c>card_declined</c>). Either lands one <see cref="Step"/> after the place,
/// with nothing confirmed, despatched or delivered. A product the fake was never told about takes the happy path.
/// </para>
/// </remarks>
public sealed class FakeOrders(TimeProvider time)
{
    internal static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>Later ids count up from here, clear of the fakes' fixed product and order ids.</summary>
    private const long FirstLaterId = 0x100;

    /// <summary>Later product ids count up from here, clear of the orders' and of the fakes' fixed ids.</summary>
    private const long FirstLaterProductId = 0x200;

    /// <summary>
    /// The minor units run-locally.md ("A declined payment is caused by the amount") scripts the payment simulator
    /// to decline: <c>.01</c> is <c>card_declined</c>. The simulator's other scripted amounts are not modelled here.
    /// </summary>
    internal const int DeclinedMinorUnits = 1;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Placed> placed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Product> products = new(StringComparer.OrdinalIgnoreCase);
    private long next = FirstLaterId;
    private long nextProduct = FirstLaterProductId;

    /// <summary>
    /// Publishes a product at <paramref name="amount"/> and answers with its id: <see cref="FakeGateway.PublishedProductId"/>
    /// the first time, a fresh one after, so two runs at once each hold a product of their own.
    /// </summary>
    public string Publish(decimal amount)
    {
        lock (gate)
        {
            string id = products.Count == 0
                ? FakeGateway.PublishedProductId
                : "0199a1b2-0000-7000-8000-" + (nextProduct++).ToString("x12", CultureInfo.InvariantCulture);
            products[id] = new Product(amount, null);
            return id;
        }
    }

    /// <summary>Sets what Inventory holds on hand for a product this fake published; any other id changes nothing, as the fake's write answers 204 for any.</summary>
    public void Stock(string productId, int onHand)
    {
        lock (gate)
        {
            if (products.TryGetValue(productId, out Product? product))
            {
                products[productId] = product with { OnHand = onHand };
            }
        }
    }

    /// <summary>Places an order for <paramref name="productId"/> (null for none the fake knows) and answers with its id.</summary>
    public string Place(string? productId = null)
    {
        lock (gate)
        {
            string id = placed.Count == 0
                ? FakeGateway.PlacedOrderId
                : "0199a1b2-0000-7000-8000-" + (next++).ToString("x12", CultureInfo.InvariantCulture);
            placed[id] = new Placed(time.GetUtcNow(), null, EndOf(productId));
            return id;
        }
    }

    /// <summary>Reservation comes before payment in the saga, so a product with no stock ends out of stock whatever it costs.</summary>
    private string? EndOf(string? productId)
    {
        if (productId is null || !products.TryGetValue(productId, out Product? product))
        {
            return null;
        }

        if (product.OnHand is < 1)
        {
            return "out_of_stock";
        }

        // The fraction is scaled, not the whole amount, which overflows decimal for a huge price; an amount with a
        // third decimal place has no minor units to decline on.
        return Math.Abs(product.Amount) % 1m * 100m == DeclinedMinorUnits ? "declined" : null;
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
        bool scripted = order.Ended is not null;
        DateTimeOffset? cancelled = scripted ? (p + Step <= now ? p + Step : null)
            : order.CancelAskedAt is { } c && c + Step < p + (2 * Step) && c + Step <= now ? c + Step : null;
        DateTimeOffset? confirmed = scripted ? null : Reached(p + Step, now, cancelled);
        DateTimeOffset? dispatched = scripted ? null : Reached(p + (2 * Step), now, cancelled);
        DateTimeOffset? delivered = scripted ? null : Reached(p + (3 * Step), now, cancelled);

        string status = delivered is not null ? "delivered"
            : cancelled is not null ? order.Ended ?? "cancelled"
            : dispatched is not null ? "dispatched"
            : confirmed is not null ? "confirmed"
            : "placed";

        DateTimeOffset asOf = new[] { p, confirmed, dispatched, delivered, cancelled }.Max()!.Value;
        var money = new { amount = 19.99m, currency = "EUR" };
        var timeline = new { placed = p, confirmed, dispatched, delivered, cancelled };
        bool cancellable = !scripted && status is "placed" or "confirmed";

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

    private sealed record Placed(DateTimeOffset At, DateTimeOffset? CancelAskedAt, string? Ended);

    private sealed record Product(decimal Amount, int? OnHand);
}
