using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The backend's Compose tree against what this console configures: a URL per surface in
/// <see cref="AdminOptions"/>, the broker's service name, and the Logs screen's service list.
/// </summary>
public sealed partial class ComposeDriftTests
{
    /// <summary>The Compose service behind each configured URL. A port there is the backend's; the URL here copies it.</summary>
    private static readonly Dictionary<string, Func<AdminOptions, string>> Surfaces = new(StringComparer.Ordinal)
    {
        ["gateway"] = o => o.GatewayUrl,
        ["catalog-api"] = o => o.CatalogUrl,
        ["ordering-api"] = o => o.OrderingUrl,
        ["web-bff"] = o => o.BffUrl,
        ["keycloak"] = o => o.KeycloakUrl,
        ["grafana"] = o => o.GrafanaUrl,
    };

    /// <summary>
    /// Service units the console has no surface for yet, each with the issue that gives it one. The
    /// issue that adds the surface deletes its row, and a unit the backend adds that is in neither
    /// table fails the build the day it merges.
    /// </summary>
    private static readonly Dictionary<string, string> UnitsWithoutSurface = new(StringComparer.Ordinal)
    {
        ["inventory"] = "#51",
        ["payments"] = "#51",
        ["shipping"] = "#59",
        ["notifications"] = "#65",
    };

    [Fact]
    public void Every_service_unit_has_a_configured_surface_or_a_named_exception()
    {
        HashSet<string> surfaced = SurfacedUnits();

        Compose.Units().Where(u => !surfaced.Contains(u) && !UnitsWithoutSurface.ContainsKey(u)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_named_exception_is_a_unit_that_still_has_no_surface()
    {
        HashSet<string> surfaced = SurfacedUnits();

        UnitsWithoutSurface.Keys.Where(u => !Compose.Units().Contains(u)).ShouldBeEmpty("an exception for a unit the backend no longer has");
        UnitsWithoutSurface.Keys.Where(surfaced.Contains).ShouldBeEmpty("an exception for a unit that now has a surface");
    }

    [Fact]
    public void Every_configured_url_names_the_host_port_its_compose_service_binds()
    {
        AdminOptions options = new();
        Dictionary<string, ComposeUnitService> services = Compose.Services().ToDictionary(s => s.Name, StringComparer.Ordinal);

        foreach ((string service, Func<AdminOptions, string> url) in Surfaces)
        {
            services.ShouldContainKey(service);
            services[service].HostPort.ShouldBe(new Uri(url(options)).Port, service);
        }
    }

    [Fact]
    public void The_broker_service_is_one_the_backend_composes()
    {
        Compose.Services().Select(s => s.Name).ShouldContain(BrokerService.Service);
    }

    [Fact]
    public void The_logs_screen_lists_every_compose_service_and_no_other()
    {
        string source = File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Web", "src", "app", "features", "logs", "logs-page.ts"));
        Match list = ComposeServicesLiteral().Match(source);
        list.Success.ShouldBeTrue("COMPOSE_SERVICES is no longer an array literal this test can read");

        string[] listed = [.. QuotedName().Matches(list.Groups[1].Value).Select(m => m.Groups[1].Value)];

        listed.Order(StringComparer.Ordinal).ShouldBe(Compose.Services().Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    private static HashSet<string> SurfacedUnits() =>
        [.. Compose.Services().Where(s => Surfaces.ContainsKey(s.Name) && s.Unit.Length > 0).Select(s => s.Unit)];

    [GeneratedRegex(@"export const COMPOSE_SERVICES = \[(.*?)\];", RegexOptions.Singleline)]
    private static partial Regex ComposeServicesLiteral();

    [GeneratedRegex(@"'([a-z0-9-]+)'")]
    private static partial Regex QuotedName();
}
