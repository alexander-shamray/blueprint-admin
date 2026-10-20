using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Stack;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The backend's Compose tree against what this console configures: a URL per surface in
/// <see cref="AdminOptions"/>, the broker's service name, the Logs screen's service list and the fakes' recorded
/// Compose model.
/// </summary>
public sealed partial class ComposeDriftTests
{
    /// <summary>The Compose service behind each configured URL. A port there is the backend's; the URL here copies it.</summary>
    private static readonly Dictionary<string, Func<AdminOptions, string>> Surfaces = new(
        PlatformServices.WithUrl.Select(s => KeyValuePair.Create(s.ComposeService, s.Url!)).Concat(
        [
            KeyValuePair.Create<string, Func<AdminOptions, string>>("psp-simulator", o => o.PaymentSimulatorUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("carrier-simulator", o => o.CarrierSimulatorUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("object-store", o => o.ObjectStoreUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("image-edge", o => o.ImageEdgeUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("keycloak", o => o.KeycloakUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("grafana", o => o.GrafanaUrl),
            KeyValuePair.Create<string, Func<AdminOptions, string>>("mailpit", o => o.MailSinkUrl),
        ]),
        StringComparer.Ordinal);

    /// <summary>
    /// Service units the console has no surface for yet, each with the issue that gives it one. The
    /// issue that adds the surface deletes its row, and a unit the backend adds that is in neither
    /// table fails the build the day it merges.
    /// </summary>
    private static readonly Dictionary<string, string> UnitsWithoutSurface = new(StringComparer.Ordinal);

    [Fact]
    public void Every_service_unit_has_a_configured_surface_or_a_named_exception()
    {
        HashSet<string> surfaced = SurfacedUnits();

        Compose.Units().ShouldNotBeEmpty("the index's include list no longer reads as this parser expects");
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
            services[service].HostPorts.ShouldContain(new Uri(url(options)).Port, service);
        }
    }

    [Fact]
    public void The_broker_service_is_one_the_backend_composes()
    {
        Compose.Services().Select(s => s.Name).ShouldContain(BrokerService.Service);
    }

    [Fact]
    public void The_doctors_gateway_is_a_compose_service_whose_unit_sets_the_cors_origins_it_reads()
    {
        ComposeUnitService gateway = Compose.Services().Where(s => s.Name == WorkstationDoctor.GatewayService).ShouldHaveSingleItem();

        string unit = Backend.Read("deploy", "compose", "services", $"{gateway.Unit}.yml");
        unit.ShouldContain("Cors__Origins__0:");
        unit.ShouldContain($"{WorkstationDoctor.CorsSwitch}:");
        Backend.Read("src", "Gateway", "Gateway.Api", "Program.cs")
            .ShouldContain($"GetValue<bool>(\"{WorkstationDoctor.CorsSwitch.Replace("__", ":", StringComparison.Ordinal)}\")");
    }

    [Fact]
    public void Every_service_a_scenario_lease_changes_is_one_the_backend_composes()
    {
        HashSet<string> services = [.. Compose.Services().Select(s => s.Name)];

        ScenarioControl.Table.Values.Select(c => c.Service).Where(s => !services.Contains(s)).ShouldBeEmpty();
    }

    [Fact]
    public void The_short_timeouts_override_is_a_compose_file_that_sets_the_saga_waits_of_the_service_the_lease_recreates()
    {
        string file = Backend.Read("deploy", "compose", ComposeService.ShortTimeoutsFileName);
        string recreated = ScenarioControl.Table[ScenarioControl.ShortWaits].Service;

        file.ShouldContain($"{recreated}:");
        file.ShouldContain("Saga__StockTimeoutDelay:");
        file.ShouldContain("Saga__ReleaseTimeoutDelay:");
        file.ShouldContain("Saga__PaymentTimeoutDelay:");
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

    /// <summary>
    /// The fakes' recorded Compose model names the services the backend composes, so a service it adds fails here
    /// until the fakes are re-recorded. FakePlatformTests holds the ps recording to this model.
    /// </summary>
    [Fact]
    public void The_recorded_compose_model_lists_every_compose_service_and_no_other()
    {
        using JsonDocument model = JsonDocument.Parse(WorkstationRecordings.ComposeConfig);
        string[] recorded = [.. model.RootElement.GetProperty("services").EnumerateObject().Select(s => s.Name)];

        recorded.Order(StringComparer.Ordinal).ShouldBe(Compose.Services().Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    private static HashSet<string> SurfacedUnits() =>
        [.. Compose.Services().Where(s => Surfaces.ContainsKey(s.Name) && s.Unit.Length > 0).Select(s => s.Unit)];

    [GeneratedRegex(@"export const COMPOSE_SERVICES = \[(.*?)\];", RegexOptions.Singleline)]
    private static partial Regex ComposeServicesLiteral();

    [GeneratedRegex(@"'([a-z0-9-]+)'")]
    private static partial Regex QuotedName();
}
