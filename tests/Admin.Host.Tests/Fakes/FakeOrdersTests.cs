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
    public void A_cancel_a_tick_before_the_confirmation_turns_into_a_cancellation_just_before_despatch()
    {
        FakeOrders orders = new(time);
        DateTimeOffset placed = time.GetUtcNow();
        string id = orders.Place();
        time.Advance(FakeOrders.Step - TimeSpan.FromTicks(1));
        orders.Cancel(id);
        time.Advance(FakeOrders.Step * 4);

        using JsonDocument read = JsonDocument.Parse(orders.Detail(id)!);
        JsonElement timeline = read.RootElement.GetProperty("timeline");

        read.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
        timeline.GetProperty("confirmed").GetDateTimeOffset().ShouldBe(placed + FakeOrders.Step);
        timeline.GetProperty("cancelled").GetDateTimeOffset().ShouldBe(placed + (FakeOrders.Step * 2) - TimeSpan.FromTicks(1));
        timeline.GetProperty("dispatched").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void A_cancel_that_would_take_effect_at_or_after_despatch_never_does(double stepsAfterPlace)
    {
        FakeOrders orders = new(time);
        string id = orders.Place();
        time.Advance(FakeOrders.Step * stepsAfterPlace);
        orders.Cancel(id);

        string[] seen = [.. Enumerable.Range(0, 4).Select(_ => Advance(orders, id))];

        seen.ShouldNotContain("cancelled");
        seen[^1].ShouldBe("delivered");
        using JsonDocument read = JsonDocument.Parse(orders.Detail(id)!);
        read.RootElement.GetProperty("timeline").GetProperty("cancelled").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_second_cancel_does_not_move_the_first_so_a_cancelled_order_stays_cancelled()
    {
        FakeOrders orders = new(time);
        DateTimeOffset placed = time.GetUtcNow();
        string id = orders.Place();
        orders.Cancel(id);
        time.Advance(FakeOrders.Step * 1.5);
        orders.Cancel(id);
        time.Advance(FakeOrders.Step * 4);

        using JsonDocument read = JsonDocument.Parse(orders.Detail(id)!);

        read.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
        read.RootElement.GetProperty("timeline").GetProperty("cancelled").GetDateTimeOffset().ShouldBe(placed + FakeOrders.Step);
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

    [Fact]
    public void A_product_published_at_a_declined_amount_ends_its_order_declined_with_nothing_confirmed()
    {
        FakeOrders orders = new(time);
        string product = orders.Publish(10.01m);
        orders.Stock(product, 10);
        DateTimeOffset placed = time.GetUtcNow();
        string id = orders.Place(product);

        string[] seen = [.. Enumerable.Range(0, 4).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["placed", "declined", "declined", "declined"]);
        using JsonDocument read = JsonDocument.Parse(orders.Detail(id)!);
        JsonElement timeline = read.RootElement.GetProperty("timeline");
        timeline.GetProperty("cancelled").GetDateTimeOffset().ShouldBe(placed + FakeOrders.Step);
        timeline.GetProperty("confirmed").ValueKind.ShouldBe(JsonValueKind.Null);
        read.RootElement.GetProperty("payment").ValueKind.ShouldBe(JsonValueKind.Null);
        read.RootElement.GetProperty("cancellable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void A_product_with_no_stock_ends_its_order_out_of_stock_even_at_a_declined_amount()
    {
        FakeOrders orders = new(time);
        string product = orders.Publish(10.01m);
        orders.Stock(product, 0);
        string id = orders.Place(product);

        string[] seen = [.. Enumerable.Range(0, 3).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["placed", "out_of_stock", "out_of_stock"]);
    }

    [Fact]
    public void A_cancel_asked_of_a_scripted_order_changes_nothing()
    {
        FakeOrders orders = new(time);
        string product = orders.Publish(19.99m);
        orders.Stock(product, 0);
        string id = orders.Place(product);
        orders.Cancel(id);

        string[] seen = [.. Enumerable.Range(0, 3).Select(_ => Advance(orders, id))];

        seen.ShouldBe(["placed", "out_of_stock", "out_of_stock"]);
    }

    [Theory]
    [InlineData(19.99, 10)]
    [InlineData(19.99, null)]
    [InlineData(10.02, 10)]
    public void A_product_the_script_does_not_name_takes_the_happy_path(double amount, int? onHand)
    {
        FakeOrders orders = new(time);
        string product = orders.Publish((decimal)amount);

        if (onHand is { } count)
        {
            orders.Stock(product, count);
        }

        string id = orders.Place(product);
        time.Advance(FakeOrders.Step * 3);

        Status(orders.Detail(id)!).ShouldBe("delivered");
    }

    [Fact]
    public void A_product_the_fake_never_published_is_not_scripted_and_its_stock_write_is_ignored()
    {
        FakeOrders orders = new(time);
        orders.Stock(FakeGateway.StockedProductId, 0);
        string id = orders.Place(FakeGateway.StockedProductId);
        time.Advance(FakeOrders.Step * 3);

        Status(orders.Detail(id)!).ShouldBe("delivered");
    }

    [Fact]
    public void The_first_product_is_the_recorded_id_and_each_later_one_its_own()
    {
        FakeOrders orders = new(time);

        string first = orders.Publish(19.99m);
        string second = orders.Publish(10.01m);

        first.ShouldBe(FakeGateway.PublishedProductId);
        second.ShouldNotBe(first);
        Guid.TryParse(second, out _).ShouldBeTrue();
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
