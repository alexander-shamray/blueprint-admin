using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Mail;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakeMailTests
{
    private const string Run = "scenario-abc-order";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_scenario_order_sends_one_message_for_each_event_it_has_raised_and_no_more()
    {
        FakeOrders orders = new(time);
        string id = orders.Place(correlationId: Run);

        orders.Mail().Select(m => m.Template).ShouldBe(["order-placed"]);

        time.Advance(FakeOrders.Step * 3);

        FakeMail[] mail = [.. orders.Mail()];
        mail.Select(m => m.Template).ShouldBe(["order-placed", "order-confirmed", "shipment-dispatched", "shipment-delivered"]);
        mail.ShouldAllBe(m => m.CorrelationId == id, "Notifications stamps the order's own id, never the id the request carried");
        mail.Select(m => m.MessageId).Distinct().Count().ShouldBe(4);

        time.Advance(FakeOrders.Step * 3);

        orders.Mail().Count.ShouldBe(4, "a delivered order raises nothing more");
    }

    [Fact]
    public void An_order_cancelled_before_it_is_confirmed_sends_its_placing_and_its_cancellation_and_nothing_between()
    {
        FakeOrders orders = new(time);
        string id = orders.Place(correlationId: Run);
        time.Advance(FakeOrders.Step / 10);
        orders.Cancel(id);
        time.Advance(FakeOrders.Step * 3);

        orders.Mail().Select(m => m.Template).ShouldBe(["order-placed", "order-cancelled"]);
    }

    [Fact]
    public void A_declined_payment_sends_the_cancellation_the_saga_compensates_with_and_the_decline()
    {
        FakeOrders orders = new(time);
        string product = orders.Publish(10.01m);
        orders.Stock(product, 10);
        orders.Place(product, correlationId: Run);
        time.Advance(FakeOrders.Step * 3);

        orders.Mail().Select(m => m.Template).Order().ShouldBe(["order-cancelled", "order-placed", "payment-declined"]);
    }

    [Fact]
    public void A_reservation_that_fails_sends_the_placing_and_the_cancellation()
    {
        FakeOrders orders = new(time);
        string product = orders.Publish(10m);
        orders.Stock(product, 0);
        orders.Place(product, correlationId: Run);
        time.Advance(FakeOrders.Step * 3);

        orders.Mail().Select(m => m.Template).Order().ShouldBe(["order-cancelled", "order-placed"]);
    }

    [Fact]
    public void An_order_placed_under_no_scenario_id_sends_nothing_so_the_recorded_sink_stays_as_recorded()
    {
        FakeOrders orders = new(time);
        orders.Place();
        orders.Place(correlationId: "api-screen-1");
        time.Advance(FakeOrders.Step * 3);

        orders.Mail().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_client_lists_them_beside_the_recorded_four_with_the_message_id_and_correlation_id_the_screen_counts_by()
    {
        FakeOrders orders = new(time);
        string id = orders.Place(correlationId: Run);
        time.Advance(FakeOrders.Step);

        MailListView view = await new MailSinkClient(
            new HttpClient(new FakeSink(orders)),
            Options.Create(new AdminOptions())).ListAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeTrue();
        view.Total.ShouldBe(6);
        MailSummary[] ours = [.. view.Messages.Where(m => m.CorrelationId == id)];
        ours.Select(m => m.MessageId.Split('@')[0].Split('.', 2)[1]).Order().ShouldBe(["order-confirmed", "order-placed"]);
        ours.ShouldAllBe(m => m.MessageId.EndsWith("@commerce.test", StringComparison.Ordinal));
        view.Messages.Where(m => m.CorrelationId != id).ShouldAllBe(m => m.MessageId.Contains(".shipment-", StringComparison.Ordinal) || m.MessageId.Contains(".order-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_opened_scenario_message_carries_its_correlation_id()
    {
        FakeOrders orders = new(time);
        string id = orders.Place(correlationId: Run);
        MailSinkClient client = new(new HttpClient(new FakeSink(orders)), Options.Create(new AdminOptions()));

        FakeMail only = orders.Mail().Single();
        MailMessageRead read = await client.ReadAsync(only.Id, TestContext.Current.CancellationToken);

        read.Message.ShouldNotBeNull().CorrelationId.ShouldBe(id);
        read.Message.Subject.ShouldBe(only.Subject);
    }

    private sealed class FakeSink(FakeOrders orders) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(FakeMailSink.Send(request, orders));
    }
}
