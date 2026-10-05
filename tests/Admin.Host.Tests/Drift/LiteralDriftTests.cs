using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Identity;
using Admin.Host.Trace;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The single literals this console copies, each read at the backend symbol its citation names.
/// A source match rather than a build reference, because the backend is a clone beside this one
/// and never a dependency of it.
/// </summary>
public sealed partial class LiteralDriftTests
{
    private static readonly string[] CorrelationIdExtensions = ["src", "BuildingBlocks", "Common.Web", "CorrelationIdExtensions.cs"];

    /// <summary>
    /// Grants the real realm gives that the fake does not yet, with the issue that adds them. Its row
    /// goes when that issue lands; a grant the fake has and the realm does not is never excused.
    /// </summary>
    private static readonly Dictionary<string, string> GrantsTheFakeLacks = new(StringComparer.Ordinal);

    [Fact]
    public void The_correlation_header_is_the_backends()
    {
        Backend.Read(CorrelationIdExtensions).ShouldContain($"public const string Header = \"{CorrelationId.Header}\";");
    }

    [Fact]
    public void The_correlation_id_length_bound_is_the_backends()
    {
        Backend.Read(CorrelationIdExtensions).ShouldContain($"public const int MaxSuppliedLength = {CorrelationId.MaxLength};");
    }

    [Fact]
    public void The_correlation_id_alphabet_is_the_backends()
    {
        Backend.Read(CorrelationIdExtensions).ShouldContain("!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')");
    }

    [Fact]
    public void The_log_scope_key_the_trace_joins_on_is_the_backends()
    {
        Backend.Read(CorrelationIdExtensions).ShouldContain("""["CorrelationId"] = correlationId""");
    }

    [Fact]
    public void The_token_clock_reads_the_claim_type_permissions_travel_in()
    {
        Backend.Read("src", "BuildingBlocks", "Common.Web", "PermissionClaim.cs")
            .ShouldContain($"public const string Type = \"{IdentityEndpoints.PermissionClaim}\";");
    }

    [Fact]
    public void The_simulator_still_serves_its_request_log_where_the_console_reads_it_and_prefixes_references_as_the_fake_does()
    {
        string simulator = Backend.Read("deploy", "compose", "psp-simulator", "README.md");

        simulator.ShouldContain("`/__admin/requests`");
        simulator.ShouldContain("`psp_` followed by the request's `Idempotency-Key`");
        Backend.Read("src", "Services", "Payments", "Payments.Application", "Provider", "AuthorisationRequest.cs")
            .ShouldContain("""IdempotencyKey => $"authorise:{OrderId.Value}";""");
    }

    [Fact]
    public void The_statuses_the_fake_reservation_and_payment_carry_are_the_backends()
    {
        Backend.Read("src", "Services", "Inventory", "Inventory.Domain", "Reservations", "ReservationStatus.cs")
            .ShouldContain("    Reserved,");
        Backend.Read("src", "Services", "Payments", "Payments.Domain", "Intents", "PaymentIntentStatus.cs")
            .ShouldContain("    Authorised,");
    }

    [Fact]
    public void The_management_port_BrokerService_says_it_avoids_is_the_one_compose_binds()
    {
        Backend.Read("deploy", "compose", "infrastructure.yml").ShouldContain("\"127.0.0.1:15672:15672\"");
    }

    [Theory]
    [InlineData(SpanRecogniser.ReserveStockUrn, "Inventory")]
    [InlineData(SpanRecogniser.AuthorisePaymentUrn, "Payments")]
    public void Each_contract_the_trace_recognises_by_urn_is_declared_under_that_namespace_and_name(string urn, string service)
    {
        string[] parts = urn.Split(':');
        string commands = Backend.Read("src", "BuildingBlocks", "Common.Contracts", service, "V1", "Commands.cs");

        commands.ShouldContain($"namespace {parts[2]};");
        commands.ShouldContain($"public sealed record {parts[3]}(");
    }

    [Fact]
    public void The_event_the_trace_reads_as_shippings_despatch_is_declared_under_that_namespace_and_name()
    {
        string[] parts = SpanRecogniser.OrderConfirmedUrn.Split(':');
        string contract = Backend.Read("src", "BuildingBlocks", "Common.Contracts", "Ordering", "V1", $"{parts[3]}.cs");

        contract.ShouldContain($"namespace {parts[2]};");
        contract.ShouldContain($"public sealed record {parts[3]} ");
    }

    [Fact]
    public void Shippings_service_name_is_its_host_projects_since_the_common_defaults_name_a_service_after_its_application()
    {
        string service = SpanRecogniser.ShippingService;

        Backend.Read("src", "Services", "Shipping", service, "Program.cs").ShouldContain("builder.AddCommonWebDefaults();");
        File.Exists(Path.Combine(Backend.Dir, "src", "Services", "Shipping", service, $"{service}.csproj")).ShouldBeTrue();
        Backend.Read("src", "BuildingBlocks", "Common.Web", "ObservabilityExtensions.cs").ShouldContain("string serviceName = builder.Environment.ApplicationName;");
    }

    [Fact]
    public void The_messaging_span_tags_the_trace_reads_are_those_of_the_backends_MassTransit()
    {
        Backend.Read("Directory.Packages.props")
            .ShouldContain($"<PackageVersion Include=\"MassTransit\" Version=\"{SpanRecogniser.MassTransitVersion}\" />");
    }

    [Fact]
    public void The_outbox_table_the_trace_recognises_is_the_backends()
    {
        Backend.Read("src", "BuildingBlocks", "Common.Infrastructure", "Outbox", "OutboxTable.cs")
            .ShouldContain($"\"{SpanRecogniser.OutboxTable}\"");
    }

    [Fact]
    public void The_order_reads_the_catalog_lists_are_the_bffs_routes()
    {
        string endpoints = Backend.Read("src", "BFF", "Web.Bff", "Endpoints", "OrderEndpoints.cs");

        endpoints.ShouldContain(".MapGroup(\"/v1/orders\")");
        endpoints.ShouldContain(".WithName(\"ListOrders\")");
        endpoints.ShouldContain("\"/{id:guid}\"");
        endpoints.ShouldContain(".WithName(\"GetOrder\")");
        endpoints.ShouldContain("string? cursor,");
        endpoints.ShouldContain("int limit = OrderPage.DefaultLimit");
    }

    [Fact]
    public void The_cancellations_the_scenario_stops_on_are_the_bffs_cancel_outcomes()
    {
        string outcomes = Backend.Read("src", "BFF", "Web.Bff.Persistence", "CancelOutcomes.cs");
        string[] read = ArrayLiteral(ScenarioRun(), "CANCELLATIONS");

        read.Length.ShouldBe(3);
        read.Where(o => !outcomes.Contains($" = \"{o}\";", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void The_timeline_the_scenario_reads_is_the_bffs_order_timeline()
    {
        string responses = Backend.Read("src", "BFF", "Web.Bff", "Orders", "OrderResponses.cs");
        Match record = OrderTimelineRecord().Match(responses);
        record.Success.ShouldBeTrue("OrderTimeline is no longer a positional record this test can read");

        string[] members = [.. TimelineMember().Matches(record.Groups["members"].Value).Select(m => m.Groups[1].Value.ToLowerInvariant())];

        ArrayLiteral(ScenarioRun(), "TIMELINE_STEPS").ShouldBe(members);
    }

    [Fact]
    public void The_statuses_the_fake_order_gives_are_the_bffs()
    {
        string statuses = Backend.Read("src", "BFF", "Web.Bff", "Orders", "BuyerStatuses.cs") + Backend.Read("src", "BFF", "Web.Bff.Persistence", "CancelOutcomes.cs");
        string fake = File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Fakes", "FakeOrders.cs"));

        string[] given = [.. QuotedStatus().Matches(fake).Select(m => m.Groups[1].Value).Distinct()];

        given.ShouldBe(["delivered", "cancelled", "dispatched", "confirmed", "placed"], ignoreOrder: true);
        given.Where(s => !statuses.Contains($" = \"{s}\";", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void The_fake_bffs_404_is_worded_as_the_bffs()
    {
        string errors = Backend.Read("src", "BFF", "Web.Bff", "Orders", "OrderReadErrors.cs");

        errors.ShouldContain("Error.NotFound(\"order.not_found\", \"No order with that id.\")");
        File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Fakes", "FakeGateway.cs"))
            .ShouldContain("\"code\":\"order.not_found\",\"detail\":\"No order with that id.\"");
    }

    [Fact]
    public void The_shipping_timers_the_scenario_says_it_waits_on_still_exist()
    {
        string hop = Backend.Read("src", "Services", "Shipping", "Shipping.Infrastructure", "Carrier", "CarrierHop.cs");

        hop.ShouldContain("public static readonly TimeSpan FulfilmentTick");
        hop.ShouldContain("public static readonly TimeSpan TrackingPollInterval");
        ScenarioRun().ShouldContain("`CarrierHop.FulfilmentTick`, `CarrierHop.TrackingPollInterval`");
    }

    [Fact]
    public void The_backend_still_provisions_no_grafana_datasources()
    {
        Directory.EnumerateDirectories(Path.Combine(Backend.Dir, "deploy"), "provisioning", SearchOption.AllDirectories)
            .ShouldBeEmpty("GrafanaContracts resolves datasource uids at runtime because the backend provisions none");
    }

    [Fact]
    public void The_default_realm_users_sign_in_with_the_realms_passwords()
    {
        Dictionary<string, string?> realm = RealmPasswords();

        foreach (RealmUser user in RealmUsers.Of(new AdminOptions()))
        {
            realm.ShouldContainKey(user.Username);
            realm[user.Username].ShouldBe(user.Password, user.Username);
        }
    }

    [Fact]
    public void The_fake_realm_grants_what_the_real_one_does_less_the_named_exceptions()
    {
        Dictionary<string, string[]> realm = RealmGrants();

        foreach ((string username, (string password, string[] permissions)) in FakeKeycloak.Users)
        {
            realm.ShouldContainKey(username);
            RealmPasswords()[username].ShouldBe(password, username);
            permissions.Where(p => !realm[username].Contains(p)).ShouldBeEmpty($"{username}: a fake grant the realm does not make");
        }

        string[] lacking = [.. FakeKeycloak.Users.SelectMany(u => realm[u.Key].Where(p => !u.Value.Permissions.Contains(p)).Select(p => $"{u.Key} {p}"))];
        lacking.Order(StringComparer.Ordinal).ShouldBe(GrantsTheFakeLacks.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_realm_user_with_a_password_is_one_the_fake_realm_signs_in()
    {
        // A service account has no credentials and is never offered by the identity picker.
        RealmPasswords().Where(u => u.Value is not null).Select(u => u.Key).Where(u => !FakeKeycloak.Users.ContainsKey(u)).ShouldBeEmpty();
    }

    [Fact]
    public void The_realm_and_client_the_console_signs_in_to_are_the_exports()
    {
        AdminOptions options = new();
        using JsonDocument realm = RealmExport();

        realm.RootElement.GetProperty("realm").GetString().ShouldBe(options.Realm);
        realm.RootElement.GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("clientId").GetString()).ShouldContain(options.ClientId);
    }

    private static Dictionary<string, string?> RealmPasswords()
    {
        using JsonDocument realm = RealmExport();

        return realm.RootElement.GetProperty("users").EnumerateArray().ToDictionary(
            u => u.GetProperty("username").GetString()!,
            u => u.TryGetProperty("credentials", out JsonElement credentials)
                ? credentials.EnumerateArray().Select(c => c.GetProperty("value").GetString()).FirstOrDefault()
                : null,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> RealmGrants()
    {
        using JsonDocument realm = RealmExport();

        return realm.RootElement.GetProperty("users").EnumerateArray().ToDictionary(
            u => u.GetProperty("username").GetString()!,
            u => u.TryGetProperty("clientRoles", out JsonElement roles) && roles.TryGetProperty("commerce-api", out JsonElement api)
                ? [.. api.EnumerateArray().Select(r => r.GetString()!)]
                : Array.Empty<string>(),
            StringComparer.Ordinal);
    }

    private static JsonDocument RealmExport() => JsonDocument.Parse(Backend.Read("deploy", "compose", "keycloak", "realm-export.json"));

    private static string ScenarioRun() =>
        File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Web", "src", "app", "features", "scenario", "scenario-run.ts"));

    /// <summary>The single-quoted strings of a TypeScript <c>const NAME … = [ … ];</c>, in order.</summary>
    private static string[] ArrayLiteral(string source, string name)
    {
        Match literal = Regex.Match(source, $@"const {name}\b[^=]*=\s*\[(?<items>[^\]]*)\]");
        literal.Success.ShouldBeTrue($"{name} is no longer an array literal this test can read");

        return [.. Regex.Matches(literal.Groups["items"].Value, "'([^']*)'").Select(m => m.Groups[1].Value)];
    }

    [GeneratedRegex(@"public sealed record OrderTimeline\((?<members>[^)]*)\);")]
    private static partial Regex OrderTimelineRecord();

    [GeneratedRegex(@"DateTimeOffset\? (\w+)")]
    private static partial Regex TimelineMember();

    [GeneratedRegex("\"(placed|confirmed|dispatched|delivered|cancelled|out_of_stock|declined)\"")]
    private static partial Regex QuotedStatus();
}
