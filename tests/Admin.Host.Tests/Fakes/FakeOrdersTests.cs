using System.Text.Json;
using Admin.Host.Fakes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakeOrdersTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void An_order_never_placed_is_the_bffs_404_and_an_empty_page()
    {
        FakeOrders orders = new(time);

        orders.Detail(FakeGateway.PlacedOrderId).ShouldBeNull();
        Items(orders.Page()).ShouldBeEmpty();
    }

    [Fact]
    public void A_placed_order_advances_one_step_at_a_time_to_delivered_and_stays_there()
    {
        FakeOrders orders = new(time);
        DateTimeOffset placed = time.GetUtcNow();
        string id = orders.Place();

        string[] seen = [.. Enumerable.Range(0, 5).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["placed", "confirmed", "dispatched", "delivered", "delivered"]);

        using JsonDocument delivered = JsonDocument.Parse(orders.Detail(id)!);
        JsonElement timeline = delivered.RootElement.GetProperty("timeline");
        timeline.GetProperty("cancelled").ValueKind.ShouldBe(JsonValueKind.Null);
        timeline.GetProperty("delivered").GetDateTimeOffset().ShouldBe(placed + (FakeOrders.Step * 3));
        delivered.RootElement.GetProperty("shipment").GetProperty("deliveredAt").GetDateTimeOffset()
            .ShouldBe(timeline.GetProperty("delivered").GetDateTimeOffset());
        delivered.RootElement.GetProperty("cancellable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void A_cancel_before_despatch_ends_the_order_cancelled_and_nothing_follows_it()
    {
        FakeOrders orders = new(time);
        string id = orders.Place();
        orders.Cancel(id);

        string[] seen = [.. Enumerable.Range(0, 5).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["placed", "cancelled", "cancelled", "cancelled", "cancelled"]);

        using JsonDocument cancelled = JsonDocument.Parse(orders.Detail(id)!);
        JsonElement timeline = cancelled.RootElement.GetProperty("timeline");
        timeline.GetProperty("confirmed").ValueKind.ShouldBe(JsonValueKind.Null);
        timeline.GetProperty("dispatched").ValueKind.ShouldBe(JsonValueKind.Null);
        cancelled.RootElement.GetProperty("shipment").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_cancel_that_arrives_after_despatch_changes_nothing()
    {
        FakeOrders orders = new(time);
        string id = orders.Place();
        time.Advance(FakeOrders.Step * 2);
        orders.Cancel(id);

        string[] seen = [.. Enumerable.Range(0, 3).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["dispatched", "delivered", "delivered"]);
    }

    [Fact]
    public void The_first_order_is_the_recorded_id_and_each_later_one_its_own_watched_apart()
    {
        FakeOrders orders = new(time);

        string first = orders.Place();
        string second = orders.Place();
        orders.Cancel(second);
        orders.Cancel("0199a1b2-0000-7000-8000-0000000000ff");
        time.Advance(FakeOrders.Step * 3);

        first.ShouldBe(FakeGateway.PlacedOrderId);
        second.ShouldNotBe(first);
        Guid.TryParse(second, out _).ShouldBeTrue();
        Status(orders.Detail(first)!).ShouldBe("delivered");
        Status(orders.Detail(second)!).ShouldBe("cancelled");
        orders.Detail("0199a1b2-0000-7000-8000-0000000000ff").ShouldBeNull();
    }

    [Fact]
    public void The_page_lists_every_order_newest_first_as_summaries()
    {
        FakeOrders orders = new(time);
        string first = orders.Place();
        time.Advance(FakeOrders.Step);
        string second = orders.Place();

        JsonElement[] items = Items(orders.Page());

        items.Select(i => i.GetProperty("orderId").GetString()).ShouldBe([second, first]);
        items[0].TryGetProperty("payment", out _).ShouldBeFalse();
        items[0].GetProperty("lines")[0].TryGetProperty("quantity", out _).ShouldBeFalse();
    }

    /// <summary>The status now, then one step on.</summary>
    private string Advance(FakeOrders orders, string id)
    {
        string status = Status(orders.Detail(id)!);
        time.Advance(FakeOrders.Step);
        return status;
    }

    private static string Status(string detail)
    {
        using JsonDocument document = JsonDocument.Parse(detail);
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static JsonElement[] Items(string page)
    {
        using JsonDocument document = JsonDocument.Parse(page);
        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }
}
