using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Identity;
using Admin.Host.Mail;
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

    /// <summary>
    /// The sink's API paths and shapes are those of one Mailpit version, so a bump in the Compose unit fails here
    /// until <see cref="MailSinkClient"/> has been checked against the new one.
    /// </summary>
    [Fact]
    public void The_mail_sink_is_the_mailpit_version_the_client_was_checked_against()
    {
        Backend.Read("deploy", "compose", "services", "notifications.yml").ShouldContain($"image: {MailSinkClient.Image}");
    }

    /// <summary>The Mail screen's Trace link is the header Notifications writes on every message it sends.</summary>
    [Fact]
    public void The_header_the_mail_screen_joins_on_is_the_one_notifications_sends()
    {
        Backend.Read("src", "Services", "Notifications", "Notifications.Infrastructure", "Mail", "SmtpMailChannel.cs")
            .ShouldContain($"internal const string CorrelationIdHeader = \"{CorrelationId.Header}\";");
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
    public void Shippings_service_name_is_its_host_projects_since_the_common_defaults_fall_back_to_the_application_and_compose_names_none()
    {
        const string serviceNameKey = "OTEL_SERVICE_NAME";
        string service = SpanRecogniser.ShippingService;
        string observability = Backend.Read("src", "BuildingBlocks", "Common.Web", "ObservabilityExtensions.cs");

        Backend.Read("src", "Services", "Shipping", service, "Program.cs").ShouldContain("builder.AddCommonWebDefaults();");
        File.Exists(Path.Combine(Backend.Dir, "src", "Services", "Shipping", service, $"{service}.csproj")).ShouldBeTrue();
        observability.ShouldContain($"public const string ServiceNameKey = \"{serviceNameKey}\";");
        observability.ShouldContain("string assembly = builder.Environment.ApplicationName;");
        observability.ShouldContain("string serviceName = builder.Configuration[ServiceNameKey] is { } named");
        observability.ShouldContain("            : assembly;");

        // The fallback is the one that applies only while nothing local names the service: a Compose file, or the
        // host's own Dockerfile or appsettings, that set the variable would make Shipping's spans carry that value.
        string[] namers = [Path.Combine(Backend.Dir, "deploy", "compose"), Path.Combine(Backend.Dir, "src", "Services", "Shipping", service)];
        namers
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            .Where(f => File.ReadAllText(f).Contains(serviceNameKey, StringComparison.Ordinal))
            .ShouldBeEmpty();
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
        IReadOnlyList<ApiOperation> curated = CuratedOperations.All(new AdminOptions());
        ApiOperation list = curated.Single(o => o.Id == "bff:ListOrders");
        ApiOperation get = curated.Single(o => o.Id == "bff:GetOrder");

        list.Url.ShouldEndWith("/bff/v1/orders");
        get.Url.ShouldEndWith("/bff/v1/orders/{id}");
        endpoints.ShouldContain(".MapGroup(\"/v1/orders\")");
        endpoints.ShouldContain($".WithName(\"{list.Name}\")");
        endpoints.ShouldContain($".WithName(\"{get.Name}\")");
        endpoints.ShouldContain($"\"/{{{get.PathParameters.Single().Name}:guid}}\"");
        list.QueryParameters.Select(p => p.Name).ShouldBe(["cursor", "limit"]);
        endpoints.ShouldContain("string? cursor,");
        endpoints.ShouldContain("int limit = OrderPage.DefaultLimit");
    }

    [Fact]
    public void The_saga_read_the_strip_makes_is_orderings_route_name_permission_and_view()
    {
        string endpoints = Backend.Read("src", "Services", "Ordering", "Ordering.Api", "Endpoints", "OrderEndpoints.cs");
        string view = Backend.Read("src", "Services", "Ordering", "Ordering.Application", "Orders", "GetSagaState", "SagaStateView.cs");
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Fakes", "fixtures", "openapi-ordering.json")));
        JsonElement saga = fixture.RootElement.GetProperty("paths").GetProperty("/v1/orders/{id}/saga").GetProperty("get");

        endpoints.ShouldContain(".MapGroup(\"/v1/orders\")");
        endpoints.ShouldContain("\"/{id:guid}/saga\"");
        endpoints.ShouldContain($".WithName(\"{saga.GetProperty("operationId").GetString()}\")");
        endpoints.ShouldContain(".RequireAuthorization(OrderingPermissions.SagaRead)");
        Backend.Read("src", "Services", "Ordering", "Ordering.Api", "OrderingPermissions.cs").ShouldContain("\"orders:saga-read\"");
        view.ShouldContain("record SagaStateView(");
        view.ShouldContain("Guid OrderId,");
        view.ShouldContain("string CurrentState,");
        view.ShouldContain("DateTimeOffset StartedAt,");
        view.ShouldContain("string? CancelReason)");
        ScenarioRun().ShouldContain("typeof o['currentState'] !== 'string' || typeof o['startedAt'] !== 'string'");
        ScenarioRun().ShouldContain("const reason = o['cancelReason'];");
        ScenarioRun().ShouldContain("saga: 'ordering:GetOrderSagaState'");
    }

    /// <summary>
    /// <c>SAGA_PAIRS</c> is §9.6's table, row for row: each saga state (no row is the last), the buyer statuses that are its
    /// own at rest, and those that can run ahead of the saga. A cell's statuses are the lower-case code spans in it; a
    /// cancellation written as words stands for all three.
    /// </summary>
    [Fact]
    public void The_saga_pairs_the_strip_judges_by_are_the_backends_table()
    {
        string messaging = Backend.Read("docs", "backend-architecture", "09-messaging.md");
        int heading = messaging.IndexOf("### Reading the saga's state", StringComparison.Ordinal);
        heading.ShouldBeGreaterThanOrEqualTo(0, "09-messaging.md no longer has the section this table is copied from");

        string[] rows = [.. messaging[heading..].Split('\n')
            .SkipWhile(l => !l.StartsWith("| Saga `currentState`", StringComparison.Ordinal))
            .Skip(2)
            .TakeWhile(l => l.StartsWith('|'))];

        rows.ShouldNotBeEmpty("the table under 'Reading the saga's state' is no longer one this test can read");

        List<(string?, string[], string[])> declared = [];

        foreach (string row in rows)
        {
            string[] cells = [.. row.Split('|', StringSplitOptions.TrimEntries).Skip(1).Take(3)];
            string[] states = [.. Regex.Matches(cells[0], "`([A-Za-z]+)`").Select(m => m.Groups[1].Value)];
            string?[] owners = states.Length == 0 ? [null] : [.. states];

            foreach (string? state in owners)
            {
                declared.Add((state, Statuses(cells[1]), Statuses(cells[2])));
            }
        }

        List<(string?, string[], string[])> copied = [.. Regex.Matches(
                ScenarioRun(),
                @"\[(?<state>null|'[A-Za-z]+'), \[(?<rest>[^\]]*)\], \[(?<ahead>[^\]]*)\]\],")
            .Select(m => (
                m.Groups["state"].Value == "null" ? null : m.Groups["state"].Value.Trim('\''),
                Quoted(m.Groups["rest"].Value),
                Quoted(m.Groups["ahead"].Value)))];

        copied.Count.ShouldBe(declared.Count);

        foreach ((string? state, string[] rest, string[] ahead) in declared)
        {
            (string?, string[], string[]) found = copied.Single(c => c.Item1 == state);

            found.Item2.OrderBy(s => s, StringComparer.Ordinal).ShouldBe(rest.OrderBy(s => s, StringComparer.Ordinal), $"{state ?? "no row"}: at rest");
            found.Item3.OrderBy(s => s, StringComparer.Ordinal).ShouldBe(ahead.OrderBy(s => s, StringComparer.Ordinal), $"{state ?? "no row"}: ahead");
        }
    }

    /// <summary>The statuses a cell names before any colon, after which the table explains rather than lists.</summary>
    private static string[] Statuses(string cell)
    {
        string listed = cell.Split(':')[0];

        return
        [
            .. Regex.Matches(listed, "`([a-z_]+)`").Select(m => m.Groups[1].Value),
            .. listed.Contains("a cancellation", StringComparison.Ordinal) ? new[] { "cancelled", "out_of_stock", "declined" } : [],
        ];
    }

    private static string[] Quoted(string items) => [.. Regex.Matches(items, "'([^']*)'").Select(m => m.Groups[1].Value)];

    [Fact]
    public void The_cancellations_the_scenario_stops_on_are_the_bffs_cancel_outcomes()
    {
        string outcomes = Backend.Read("src", "BFF", "Web.Bff.Persistence", "CancelOutcomes.cs");
        string[] declared = [.. ConstantValue().Matches(outcomes).Select(m => m.Groups["value"].Value)];

        declared.ShouldNotBeEmpty("CancelOutcomes no longer declares string constants this test can read");
        ArrayLiteral(ScenarioRun(), "CANCELLATIONS").ShouldBe(declared, ignoreOrder: true);
    }

    /// <summary>
    /// The Scenario's mail count names each message's event from its template key, so the keys are Notifications' own and each is
    /// recorded by the handler of the event the table pairs it with; and a message's <c>Message-ID</c> is still the event's id
    /// and the key, which is what the count reads the event from.
    /// </summary>
    [Fact]
    public void The_mail_the_scenario_counts_is_the_templates_and_events_notifications_sends()
    {
        string keys = Backend.Read("src", "Services", "Notifications", "Notifications.Application", "Rendering", "TemplateKeys.cs");
        Dictionary<string, string> constants = TemplateKey().Matches(keys).ToDictionary(m => m.Groups["value"].Value, m => m.Groups["name"].Value);

        constants.ShouldNotBeEmpty("TemplateKeys no longer declares string constants this test can read");

        string[] table = ArrayLiteral(ScenarioRun(), "MAIL_EVENTS");
        string[] templates = [.. table.Select(e => e.Split('=')[0])];
        templates.ShouldBe(constants.Keys, ignoreOrder: true);

        foreach (string entry in table)
        {
            string[] pair = entry.Split('=');
            string handler = Backend.Read("src", "Services", "Notifications", "Notifications.Application", "Intake", $"{pair[1]}Handler.cs");

            handler.ShouldContain($"IIntegrationEventHandler<{pair[1]}>", customMessage: entry);
            handler.ShouldContain($"TemplateKeys.{constants[pair[0]]},", customMessage: entry);
        }

        Backend.Read("src", "Services", "Notifications", "Notifications.Application", "Mail", "MailMessageId.cs")
            .ShouldContain("public string LocalPart => $\"{EventId:N}.{TemplateKey}\";");
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

    /// <summary>
    /// The three members a watch reads from the detail, under the camelCase names the web defaults write: a rename
    /// there would fail every live watch while the fake, written to the same shape, kept every test green.
    /// </summary>
    [Fact]
    public void The_members_the_scenario_reads_from_the_order_detail_are_the_bffs()
    {
        string responses = Backend.Read("src", "BFF", "Web.Bff", "Orders", "OrderResponses.cs");
        Match detail = OrderDetailRecord().Match(responses);
        detail.Success.ShouldBeTrue("OrderDetail is no longer a positional record this test can read");

        detail.Groups["members"].Value.ShouldContain("string Status,");
        detail.Groups["members"].Value.ShouldContain("OrderTimeline Timeline,");
        detail.Groups["members"].Value.ShouldContain("DateTimeOffset AsOf,");
        ScenarioRun().ShouldContain("typeof o['status'] !== 'string' || typeof o['asOf'] !== 'string'");
        ScenarioRun().ShouldContain("const t = o['timeline'];");
    }

    [Fact]
    public void The_statuses_the_fake_order_gives_are_the_bffs()
    {
        string statuses = Backend.Read("src", "BFF", "Web.Bff", "Orders", "BuyerStatuses.cs") + Backend.Read("src", "BFF", "Web.Bff.Persistence", "CancelOutcomes.cs");
        string fake = File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Fakes", "FakeOrders.cs"));

        string[] backends = [.. ConstantValue().Matches(statuses).Select(m => m.Groups["value"].Value).Distinct()];
        string[] given = [.. StatusSite().Matches(fake).Select(m => m.Groups[1].Value).Distinct()];

        backends.ShouldNotBeEmpty("no status constant was read; the backend's status files no longer fit the reader");
        given.ShouldBe(backends, ignoreOrder: true);
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
    public void The_erasure_widget_sends_as_the_realm_login_that_holds_privacy_erase_and_nothing_else()
    {
        string page = File.ReadAllText(Path.Combine(Backend.AdminRoot, "src", "Admin.Web", "src", "app", "features", "erasure", "erasure-run.ts"));
        Match identity = Regex.Match(page, @"PRIVACY_IDENTITY: Identity = \{ username: '(?<user>[^']+)'");
        identity.Success.ShouldBeTrue("PRIVACY_IDENTITY is no longer a literal this test can read");

        RealmGrants()[identity.Groups["user"].Value].ShouldBe(["privacy:erase"]);
        RealmUsers.Of(new AdminOptions()).Select(u => u.Username).ShouldContain(identity.Groups["user"].Value);
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

    [GeneratedRegex(@"public sealed record OrderDetail\((?<members>[^;]*)\);")]
    private static partial Regex OrderDetailRecord();

    [GeneratedRegex(@"DateTimeOffset\? (\w+)")]
    private static partial Regex TimelineMember();

    /// <summary>
    /// A lower-case snake literal where <c>FakeOrders</c> produces or tests an order's status: an arm of a conditional, the first
    /// of a returned pair, or either side of an equality. It names no status, so one the fake adds that the BFF lacks is read too.
    /// </summary>
    [GeneratedRegex(@"(?:\? |: |\?\? |\(|[=!]= )""([a-z]+(?:_[a-z]+)*)""(?=,|;|\s*$|\s*\?)", RegexOptions.Multiline)]
    private static partial Regex StatusSite();

    [GeneratedRegex("public const string \\w+ = \"(?<value>[^\"]*)\";")]
    private static partial Regex ConstantValue();

    [GeneratedRegex("public const string (?<name>\\w+) = \"(?<value>[^\"]*)\";")]
    private static partial Regex TemplateKey();
}
