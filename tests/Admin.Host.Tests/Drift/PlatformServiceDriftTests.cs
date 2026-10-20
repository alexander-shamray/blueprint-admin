using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="PlatformServices.All"/> against the owners that hang from it: a record missing a part, or a part
/// the record does not name, fails here rather than in a screen.
/// </summary>
public sealed class PlatformServiceDriftTests
{
    [Fact]
    public void Every_service_has_a_unique_name_and_compose_service()
    {
        PlatformServices.All.Select(s => s.Name).ShouldBeUnique();
        PlatformServices.All.Select(s => s.ComposeService).ShouldBeUnique();
        PlatformServices.All.ShouldAllBe(s => s.Name.Length > 0 && s.ComposeService.Length > 0 && s.BackendNamespace.Length > 0);
    }

    [Fact]
    public void Every_service_with_an_openapi_document_has_a_url()
    {
        PlatformServices.WithOpenApi.ShouldAllBe(s => s.Url != null);
    }

    [Fact]
    public void Every_gateway_prefix_a_record_names_is_a_row_of_the_gateway_table()
    {
        HashSet<string> rows = [.. GatewayRoutes.Routes.Select(r => r.Prefix)];

        PlatformServices.All.Where(s => s.GatewayPrefix is not null).Select(s => s.GatewayPrefix!).Where(p => !rows.Contains(p)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_gateway_row_that_fronts_a_service_is_named_by_a_record()
    {
        HashSet<string> named = [.. PlatformServices.All.Select(s => s.GatewayPrefix).OfType<string>()];

        GatewayRoutes.Routes.Select(r => r.Prefix)
            .Where(p => !named.Any(n => p.StartsWith(n, StringComparison.Ordinal)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Every_service_consumes_a_queue_and_every_queue_belongs_to_a_service()
    {
        // The gateway only routes; it declares no consumer queue.
        foreach (PlatformService service in PlatformServices.All.Where(s => s.Name != "gateway"))
        {
            PlatformQueues.Table.ShouldContain(q => q.BackendSymbol.StartsWith(service.BackendNamespace + ".", StringComparison.Ordinal), service.Name);
        }

        PlatformQueues.Table
            .Where(q => !PlatformServices.All.Any(s => q.BackendSymbol.StartsWith(s.BackendNamespace + ".", StringComparison.Ordinal)))
            .ShouldBeEmpty();
    }
}
