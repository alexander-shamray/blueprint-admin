using Admin.Host.Broker;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class ParkedQueuesTests
{
    [Theory]
    [InlineData("ordering-catalog-events_error", "error")]
    [InlineData("bff-order-events_skipped", "skipped")]
    [InlineData("ordering-catalog-events", null)]
    [InlineData("ordering-fulfilment-saga_delay", null)]
    public void A_queue_is_parked_by_its_suffix_and_by_nothing_else(string queue, string? kind)
    {
        ParkedQueues.KindOf(queue)?.Name.ShouldBe(kind);
    }

    [Theory]
    [InlineData("ordering-catalog-events_error", "ordering-catalog-events")]
    [InlineData("bff-order-events_skipped", "bff-order-events")]
    [InlineData("ordering-catalog-events", "ordering-catalog-events")]
    public void The_endpoint_of_a_queue_is_its_name_without_the_parked_suffix(string queue, string endpoint)
    {
        ParkedQueues.EndpointOf(queue).ShouldBe(endpoint);
    }

    [Theory]
    [InlineData("bff-order-events_skipped", "the BFF's order projection")]
    [InlineData("bff-order-events_error", "the BFF's order projection")]
    [InlineData("not-a-platform-queue_skipped", null)]
    public void A_parked_queue_has_its_endpoints_consumer(string queue, string? consumer)
    {
        PlatformQueues.ConsumerOf(queue).ShouldBe(consumer);
    }

    [Fact]
    public void Every_parked_kind_has_its_own_runbook()
    {
        ParkedQueues.All.Select(k => k.Runbook).Distinct().Count().ShouldBe(ParkedQueues.All.Length);
    }
}
