using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Admin.Host.Fakes;

/// <summary>What a cancel asked of <see cref="FakeOrders"/> was told: taken, or refused because Shipping had already despatched the order.</summary>
public enum CancelOutcome
{
    Accepted,
    AlreadyShipped,
}

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
/// never set, or set below one, ends it <c>out_of_stock</c>; a price whose minor units are <see cref="DeclinedMinorUnits"/> ends it
/// <c>declined</c> (the payment simulator's <c>card_declined</c>). Either lands one <see cref="Step"/> after the place,
/// with nothing confirmed, despatched or delivered. A product the fake was never told about takes the happy path.
/// </para>
/// </remarks>
public sealed class FakeOrders(TimeProvider time, FakeStack stack)
{
    /// <summary>A fake whose stack is as it was recorded: Inventory running and the saga's waits at their minutes.</summary>
    public FakeOrders(TimeProvider time)
        : this(time, new FakeStack())
    {
    }

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

    /// <summary>The minor units run-locally.md scripts the payment simulator to answer after the adapter's own budget (the payment wait).</summary>
    internal const int PaymentWaitMinorUnits = 9;

    /// <summary>
    /// The saga's waits as the fake keeps them: with the short-timeouts override a couple of the fake's own steps, without it
    /// the minutes the real ones are (stock 5, payment 15), which no watch of the Scenario outlasts.
    /// </summary>
    internal static readonly TimeSpan ShortStockWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Longer than the stock wait because the release-timeout script places a <c>.09</c> order, reads its reservation, stops
    /// Inventory and cancels, all before this lapses: a cancel that lands after it finds the order already declined.
    /// </summary>
    internal static readonly TimeSpan ShortPaymentWait = TimeSpan.FromSeconds(6);

    internal static readonly TimeSpan NormalStockWait = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan NormalPaymentWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The despatch wait (run-locally.md, "Despatch timeout"), which the real saga keeps at 60 seconds with the override and three
    /// days without: after it an order to <see cref="StrangePostalCode"/> is flagged for review, still confirmed.
    /// </summary>
    internal static readonly TimeSpan ShortDespatchWait = TimeSpan.FromSeconds(4);

    internal static readonly TimeSpan NormalDespatchWait = TimeSpan.FromDays(3);

    /// <summary>
    /// The release wait (run-locally.md, "Release timeout"), which the real saga keeps at 30 seconds with the override and ten
    /// minutes without: after it a cancelled order whose release Inventory never answered is flagged for review.
    /// </summary>
    internal static readonly TimeSpan ShortReleaseWait = TimeSpan.FromSeconds(4);

    internal static readonly TimeSpan NormalReleaseWait = TimeSpan.FromMinutes(10);

    internal const string StockNotReleasedReason = "stock_not_released";

    /// <summary>
    /// The carrier simulator's code whose parcel is booked and never collected, so the order stays confirmed until the saga's
    /// despatch wait lapses and Ordering flags it (run-locally.md, "Despatch timeout"). One of <see cref="CarrierCodes"/>.
    /// </summary>
    internal const string StrangePostalCode = "SIM-STRANGE";

    /// <summary>The reason Ordering gives when the despatch wait lapses (blueprint-backend <c>OrderReviews</c>, run-locally.md).</summary>
    internal const string NotDespatchedReason = "not_despatched";

    /// <summary>
    /// The delivery postal code run-locally.md ("The carrier's answer is scripted by the delivery postal code") gives to
    /// the carrier simulator's collected-never-delivered outcome. The simulator's README owns the codes; the others
    /// end where the order read cannot tell them from a confirmed order and are not modelled here.
    /// </summary>
    internal const string InTransitPostalCode = "SIM-TRANSIT";

    /// <summary>
    /// The carrier simulator's codes whose outcome the order read cannot tell from a confirmed order (run-locally.md's table):
    /// refused, down, slow and strange. Only the simulator's request log tells them apart.
    /// </summary>
    internal static readonly string[] CarrierCodes = ["SIM-REFUSED", "SIM-DOWN", "SIM-SLOW", "SIM-STRANGE"];

    /// <summary>
    /// The most entries the fake simulator's log shows one order, and the most orders to one code it shows, newest last, so a
    /// long-lived fake host's answer stays small and a later order is never crowded out by an earlier one.
    /// </summary>
    private const int MaxCarrierEntries = 20;

    private const int MaxCarrierOrders = 5;

    /// <summary>What an order for a product the fake was never told about shows: the recorded listing's Walnut desk.</summary>
    private const string DefaultName = "Walnut desk";

    private const decimal DefaultAmount = 19.99m;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Placed> placed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Product> products = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> uploads = new(StringComparer.Ordinal);
    private readonly HashSet<string> publishedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> addresses = new(StringComparer.Ordinal);
    private long next = FirstLaterId;

    /// <summary>The erasure requests Privacy has been asked to raise; they live beside the orders so they outlast the handlers the client factory rotates.</summary>
    internal FakeErasures Erasures { get; } = new(time);
    private long nextProduct = FirstLaterProductId;

    /// <summary>
    /// Publishes a product at <paramref name="amount"/> and answers with its id: <see cref="FakeGateway.PublishedProductId"/>
    /// the first time, a fresh one after, so two runs at once each hold a product of their own.
    /// </summary>
    public string Publish(decimal amount, string name = "Walnut desk", string? thumbnailUrl = null)
    {
        lock (gate)
        {
            string id = products.Count == 0
                ? FakeGateway.PublishedProductId
                : "0199a1b2-0000-7000-8000-" + (nextProduct++).ToString("x12", CultureInfo.InvariantCulture);
            products[id] = new Product(amount, null, name, thumbnailUrl);
            return id;
        }
    }

    /// <summary>The listing's row for a product this fake published, as Catalog's <c>GetProduct</c> answers it; null for any other id.</summary>
    public string? ProductSummary(string id)
    {
        lock (gate)
        {
            return products.TryGetValue(id, out Product? product)
                ? JsonSerializer.Serialize(new
                {
                    productId = id,
                    name = product.Name,
                    thumbnailUrl = product.ThumbnailUrl,
                    amount = product.Amount,
                    currency = "EUR",
                    publishedAt = time.GetUtcNow(),
                })
                : null;
        }
    }

    /// <summary>
    /// What the object store holds: a key the BFF issued is written when the bytes arrive, with whether they were an image,
    /// because the store keeps any bytes and Catalog is what sniffs them when it attaches the upload.
    /// </summary>
    public void Store(string key, bool isImage)
    {
        lock (gate)
        {
            uploads[key] = isImage;
        }
    }

    /// <summary>Null when nothing is stored under the key; otherwise whether what is stored is an image.</summary>
    public bool? Stored(string key)
    {
        lock (gate)
        {
            return uploads.TryGetValue(key, out bool isImage) ? isImage : null;
        }
    }

    /// <summary>Marks a published image's key as one the edge serves.</summary>
    public void ServeImage(string productKey)
    {
        lock (gate)
        {
            publishedImages.Add(productKey);
        }
    }

    public bool Serves(string productKey)
    {
        lock (gate)
        {
            return publishedImages.Contains(productKey);
        }
    }

    /// <summary>The caller's saved address, the one the BFF keeps per user; null where none was put.</summary>
    public string? Address(string subject)
    {
        lock (gate)
        {
            return addresses.GetValueOrDefault(subject);
        }
    }

    public void SaveAddress(string subject, string json)
    {
        lock (gate)
        {
            addresses[subject] = json;
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

    /// <summary>
    /// Places an order for <paramref name="productId"/> (null for none the fake knows) and answers with its id.
    /// A <paramref name="postalCode"/> of <see cref="InTransitPostalCode"/> scripts the carrier as run-locally.md's table
    /// does: collected, never delivered.
    /// </summary>
    public string Place(string? productId = null, string? postalCode = null)
    {
        lock (gate)
        {
            string id = placed.Count == 0
                ? FakeGateway.PlacedOrderId
                : "0199a1b2-0000-7000-8000-" + (next++).ToString("x12", CultureInfo.InvariantCulture);
            Product? known = productId is not null && products.TryGetValue(productId, out Product? found) ? found : null;
            (string? ended, TimeSpan after) = EndOf(productId);
            placed[id] = new Placed(
                time.GetUtcNow(),
                null,
                ended,
                known is null ? FakeGateway.StockedProductId : productId!,
                known?.Name ?? DefaultName,
                known?.Amount ?? DefaultAmount)
            {
                InTransit = string.Equals(postalCode, InTransitPostalCode, StringComparison.Ordinal),
                EndDelay = after,
                Carrier = postalCode is not null && CarrierCodes.Contains(postalCode) ? postalCode : null,
                ReviewReason = string.Equals(postalCode, StrangePostalCode, StringComparison.Ordinal) ? NotDespatchedReason : null,
                ReviewAfter = stack.ShortWaits ? ShortDespatchWait : NormalDespatchWait,
            };
            return id;
        }
    }

    /// <summary>
    /// How an order for the product ends and how long after the place. Reservation comes before payment in the saga, so a
    /// product with no stock ends out of stock whatever it costs; and with Inventory stopped nothing answers the reservation,
    /// so the order ends out of stock only when the saga's own wait lapses (<see cref="FakeStack.ShortWaits"/> says which
    /// delay: the override's seconds, or the minutes the watch will not outlast).
    /// </summary>
    private (string? Ended, TimeSpan After) EndOf(string? productId)
    {
        if (stack.InventoryStopped)
        {
            return ("out_of_stock", stack.ShortWaits ? ShortStockWait : NormalStockWait);
        }

        if (productId is null || !products.TryGetValue(productId, out Product? product))
        {
            return (null, Step);
        }

        // Null is a product Inventory has never reported a count for (run-locally.md: "set a count first"): the reservation
        // fails for it as for a count of zero, so a script that does not stock its product ends out of stock here as it does there.
        if (product.OnHand is null or < 1)
        {
            return ("out_of_stock", Step);
        }

        // The fraction is scaled, not the whole amount, which overflows decimal for a huge price; an amount with a
        // third decimal place has no minor units to decline on.
        decimal minorUnits = Math.Abs(product.Amount) % 1m * 100m;

        if (minorUnits == DeclinedMinorUnits)
        {
            return ("declined", Step);
        }

        // run-locally.md: a total ending .09 is the payment simulator answering after the adapter's own budget, which Payments
        // retries for longer than the saga waits, so the order is declined by the saga's payment wait.
        return minorUnits == PaymentWaitMinorUnits ? ("declined", stack.ShortWaits ? ShortPaymentWait : NormalPaymentWait) : (null, Step);
    }

    /// <summary>
    /// A cancel for an order never placed changes nothing and is accepted, as the fake's cancel always answered 204 for
    /// any id. One asked of an order Shipping has already despatched is refused, as the platform refuses it (422
    /// <c>order.already_shipped</c>, run-locally.md "After despatch there is nothing to compensate"), and changes nothing.
    /// </summary>
    public CancelOutcome Cancel(string orderId)
    {
        lock (gate)
        {
            if (!placed.TryGetValue(orderId, out Placed? order))
            {
                return CancelOutcome.Accepted;
            }

            if (order.Ended is null && order.At + (2 * Step) <= time.GetUtcNow() && order.CancelAskedAt is null)
            {
                return CancelOutcome.AlreadyShipped;
            }

            DateTimeOffset now = time.GetUtcNow();

            // run-locally.md, "Release timeout": a .09 order is still waiting for a payment answer that comes late, with its stock
            // reserved. A cancel that lands before the payment wait lapses ends it cancelled by the customer, and the release the
            // saga then asks of Inventory is answered, or with Inventory stopped is never answered and flagged once the release
            // wait lapses.
            if (order.Ended == "declined" && order.CancelAskedAt is null && IsPaymentWait(order) && now < order.At + order.EndDelay)
            {
                TimeSpan sincePlace = now - order.At;
                placed[orderId] = order with
                {
                    Ended = "cancelled",
                    EndDelay = sincePlace + Step,
                    CancelAskedAt = now,
                    ReviewReason = stack.InventoryStopped ? StockNotReleasedReason : null,
                    ReviewAfter = sincePlace + (stack.ShortWaits ? ShortReleaseWait : NormalReleaseWait),
                };

                return CancelOutcome.Accepted;
            }

            if (order.CancelAskedAt is null)
            {
                placed[orderId] = order with { CancelAskedAt = now };
            }

            return CancelOutcome.Accepted;
        }
    }

    /// <summary>
    /// What the carrier simulator's request log holds of the orders placed to <paramref name="code"/>, as WireMock.Net's
    /// <c>/__admin/requests/find</c> answers it, entry by entry in the shape the Scenario reads: <c>Request.DateTime</c>,
    /// <c>Method</c>, <c>Path</c> and <c>Body</c>, and <c>Response.StatusCode</c>. The cadence is the fake's own and the shapes
    /// are run-locally.md's: <c>SIM-REFUSED</c> one booking answered 422; <c>SIM-DOWN</c> repeated bookings, each 503;
    /// <c>SIM-SLOW</c> repeated bookings of one shipment, each 201; <c>SIM-STRANGE</c> one booking answered 201 and then
    /// repeated polls of the feed. <paramref name="polls"/> asks for the feed's polls, not the bookings.
    /// </summary>
    public string CarrierLog(string code, bool polls)
    {
        KeyValuePair<string, Placed>[] orders;

        lock (gate)
        {
            orders = [.. placed.Where(o => o.Value.Carrier == code).OrderBy(o => o.Value.At).TakeLast(MaxCarrierOrders)];
        }

        DateTimeOffset now = time.GetUtcNow();
        List<object> entries = [];

        foreach ((string orderId, Placed order) in orders)
        {
            Guid shipment = new(SHA256.HashData(Encoding.UTF8.GetBytes(orderId)).AsSpan(0, 16));
            string body = JsonSerializer.Serialize(new { shipmentId = shipment, address = new { line1 = "1 Test Street", city = "Almaty", postalCode = code, country = "KZ" } });
            DateTimeOffset booked = order.At + (2 * Step);

            int shown = 0;

            void Add(DateTimeOffset at, string method, string path, string? requestBody, int status, string title)
            {
                if (at <= now && shown < MaxCarrierEntries)
                {
                    shown++;
                    entries.Add(new
                    {
                        Guid = Guid.NewGuid(),
                        Request = new { DateTime = at, Method = method, Path = path, Body = requestBody },
                        Response = new { StatusCode = status },
                        MappingTitle = title,
                    });
                }
            }

            if (polls)
            {
                for (int i = 0; code == "SIM-STRANGE" && i < MaxCarrierEntries; i++)
                {
                    Add(order.At + (3 * Step) + (2 * Step * i), "GET", "/v1/shipments/crr_SIM-STRANGE/events", null, 200, "SIM-STRANGE: a status nobody agreed");
                }

                continue;
            }

            switch (code)
            {
                case "SIM-REFUSED":
                    Add(booked, "POST", "/v1/shipments", body, 422, "SIM-REFUSED is an address the carrier will not serve");
                    break;
                case "SIM-DOWN":
                    for (int i = 0; i < MaxCarrierEntries; i++)
                    {
                        Add(booked + (Step * i), "POST", "/v1/shipments", body, 503, "SIM-DOWN is a carrier that is down");
                    }

                    break;
                case "SIM-SLOW":
                    for (int i = 0; i < MaxCarrierEntries; i++)
                    {
                        Add(booked + (2 * Step * i), "POST", "/v1/shipments", body, 201, "SIM-SLOW delays past the adapter's total, then answers as booked");
                    }

                    break;
                default:
                    Add(booked, "POST", "/v1/shipments", body, 201, "SIM-STRANGE books, and its feed is a stranger's input");
                    break;
            }
        }

        return JsonSerializer.Serialize(entries);
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

    /// <summary>When each step of the order has happened by now, and the status those steps make: one clock for the BFF's read and the saga's.</summary>
    private (DateTimeOffset? Confirmed, DateTimeOffset? Dispatched, DateTimeOffset? Delivered, DateTimeOffset? Cancelled, string Status) Moments(Placed order)
    {
        DateTimeOffset p = order.At;
        DateTimeOffset now = time.GetUtcNow();
        bool scripted = order.Ended is not null;
        DateTimeOffset? cancelled = scripted ? (p + order.EndDelay <= now ? p + order.EndDelay : null)
            : order.CancelAskedAt is { } c && c + Step < p + (2 * Step) && c + Step <= now ? c + Step : null;
        DateTimeOffset? confirmed = scripted ? null : Reached(p + Step, now, cancelled);
        DateTimeOffset? dispatched = scripted || order.Carrier is not null ? null : Reached(p + (2 * Step), now, cancelled);
        DateTimeOffset? delivered = scripted || order.InTransit || order.Carrier is not null ? null : Reached(p + (3 * Step), now, cancelled);

        string status = delivered is not null ? "delivered"
            : cancelled is not null ? order.Ended ?? "cancelled"
            : dispatched is not null ? "dispatched"
            : confirmed is not null ? "confirmed"
            : "placed";

        return (confirmed, dispatched, delivered, cancelled, status);
    }

    /// <summary>
    /// Ordering's saga read (<c>GET /v1/orders/{id}/saga</c>) for <paramref name="orderId"/>, or null where it answers 404: an
    /// order the fake never placed, or one whose saga has finalised and deleted its instance. The saga's state follows the
    /// same moments as the BFF's status, so the fake's two never disagree: placed is awaiting confirmation (stock and payment
    /// are not told apart by the fake, except that an order scripted to end for stock or for payment waits for that), confirmed
    /// is confirmed, and every later status has no row.
    /// </summary>
    public string? Saga(string orderId)
    {
        Placed? order;

        lock (gate)
        {
            placed.TryGetValue(orderId, out order);
        }

        if (order is null)
        {
            return null;
        }

        string? state = Moments(order).Status switch
        {
            "placed" => order.Ended switch
            {
                "out_of_stock" => "AwaitingStock",
                "declined" => "AwaitingPayment",
                _ => "AwaitingConfirmation",
            },
            "confirmed" => "Confirmed",
            _ => null,
        };

        return state is null ? null : JsonSerializer.Serialize(new { orderId, currentState = state, startedAt = order.At, cancelReason = (string?)null });
    }

    /// <summary><c>OrderDetail</c> when <paramref name="detail"/>, else the <c>OrderSummary</c> the list carries.</summary>
    private object View(string orderId, Placed order, bool detail)
    {
        DateTimeOffset p = order.At;
        (DateTimeOffset? confirmed, DateTimeOffset? dispatched, DateTimeOffset? delivered, DateTimeOffset? cancelled, string status) = Moments(order);
        bool scripted = order.Ended is not null;

        DateTimeOffset asOf = new[] { p, confirmed, dispatched, delivered, cancelled }.Max()!.Value;
        var money = new { amount = order.Amount, currency = "EUR" };
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
                lines = new[] { new { productId = order.ProductId, productName = order.ProductName, lineTotal = money } },
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
                new { productId = order.ProductId, productName = order.ProductName, lineTotal = money, quantity = 1, unitPrice = money },
            },
            asOf,
            // The saga authorises before it confirms; the fake reports both at once.
            payment = confirmed is null ? null : new { authorisedAt = confirmed, amount = money, refundedAmount = (object?)null },
            shipment = dispatched is null ? null : new { trackingNumber = (string?)null, dispatchedAt = dispatched, deliveredAt = delivered },
        };
    }

    /// <summary>
    /// The review flags Ordering has logged for an order by now, as Loki would hold them: none for an order the fake never
    /// placed or one whose wait has not lapsed.
    /// </summary>
    public IReadOnlyList<(string Reason, DateTimeOffset At)> ReviewFlags(string orderId)
    {
        lock (gate)
        {
            if (!placed.TryGetValue(orderId, out Placed? order) || order.ReviewReason is null)
            {
                return [];
            }

            DateTimeOffset at = order.At + order.ReviewAfter;

            // run-locally.md: a cancel of a confirmed order before the wait lapses ends the saga's wait, so the despatch wait
            // never fires; what a cancel flags is a different reason (cancelled_after_confirmation), which this fake does not model.
            if (order.ReviewReason == NotDespatchedReason && order.CancelAskedAt is { } asked && asked < at)
            {
                return [];
            }

            return at <= time.GetUtcNow() ? [(order.ReviewReason, at)] : [];
        }
    }

    /// <summary>Whether the order's total ends in the minor units the payment simulator answers after the adapter's own budget.</summary>
    private static bool IsPaymentWait(Placed order) => Math.Abs(order.Amount) % 1m * 100m == PaymentWaitMinorUnits;

    /// <summary>
    /// The product an order's stock is reserved for, or null where there is no reservation: an order the fake never placed, or
    /// one whose reservation failed. A reservation is read as <c>Reserved</c> whatever became of the order, because the release a
    /// cancel asks for is the step that can go unanswered.
    /// </summary>
    public string? ReservedProduct(string orderId)
    {
        lock (gate)
        {
            return placed.TryGetValue(orderId, out Placed? order) && order.Ended != "out_of_stock" ? order.ProductId : null;
        }
    }

    /// <summary>A step happens at its time unless the order was cancelled first.</summary>
    private static DateTimeOffset? Reached(DateTimeOffset at, DateTimeOffset now, DateTimeOffset? cancelled) =>
        at <= now && (cancelled is null || at < cancelled) ? at : null;

    private sealed record Placed(DateTimeOffset At, DateTimeOffset? CancelAskedAt, string? Ended, string ProductId, string ProductName, decimal Amount)
    {
        /// <summary>The carrier collected the parcel and never delivers it: the order stays dispatched.</summary>
        public bool InTransit { get; init; }

        /// <summary>How long after the place a scripted end lands: one <see cref="Step"/> unless a saga wait is what ends it.</summary>
        public TimeSpan EndDelay { get; init; } = Step;

        /// <summary>
        /// The postal code of an order to one of the carrier outcomes the order read cannot tell apart: it is confirmed and stays
        /// so, and only the simulator's request log (<see cref="CarrierLog"/>) shows what the carrier saw.
        /// </summary>
        public string? Carrier { get; init; }

        /// <summary>The reason Ordering flags this order for review once <see cref="ReviewAfter"/> has passed, or null when it never does.</summary>
        public string? ReviewReason { get; init; }

        public TimeSpan ReviewAfter { get; init; }
    }

    private sealed record Product(decimal Amount, int? OnHand, string Name, string? ThumbnailUrl);
}
