using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>The recorded docker outputs FakePlatform mode replays. Exact text is not a contract; the shapes are.</summary>
public static class FakePlatformScripts
{
    // logs -f output as Compose prefixes it. By service: gateway 3, catalog-api 4,
    // ordering-api 3, web-bff 1, rabbitmq 1; twelve in all. A follow of named
    // services replays only their lines, as docker compose logs does.
    private static readonly string[] FollowLogLines =
    [
        "gateway       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "catalog-api   | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "ordering-api  | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "web-bff       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0001",
        "catalog-api   | info: Catalog.Api.Endpoints[1] Listed 20 products CorrelationId=fake-corr-0001",
        "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0002",
        "catalog-api   | info: Catalog.Api.Endpoints[2] Published product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f CorrelationId=fake-corr-0002",
        "catalog-api   | info: Common.Infrastructure.Outbox.OutboxDispatcher[10] Dispatched PriceChanged to catalog-events CorrelationId=fake-corr-0002",
        "ordering-api  | info: MassTransit[0] Consumed PriceChanged on ordering-catalog-events CorrelationId=fake-corr-0002",
        "ordering-api  | info: Ordering.Application.Projections[3] Priced product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f at 19.99 EUR",
        "rabbitmq      | 2026-09-14 07:00:00.000 [info] <0.1.0> connection accepted from ordering-api",
    ];

    // npm start in the frontend clone as the Angular dev server prints it. It
    // keeps running until stopped, as ng serve does.
    private static readonly string[] NgServeLines =
    [
        "> blueprint-frontend@0.0.0 start",
        "> ng serve",
        "Initial chunk files | Names  | Raw size",
        "main.js             | main   | 212.40 kB",
        "styles.css          | styles |  95.12 kB",
        "Application bundle generation complete. [2.315 seconds]",
        "Watch mode enabled. Watching for file changes...",
        "  Local:   http://localhost:5173/",
    ];

    public static FakeProcessRunner Script(FakeProcessRunner runner, string composeFile)
    {
        string prefix = $"compose -f {composeFile} ";

        return runner
            .On("docker", prefix + "ps -a --format json", 0, [.. ComposePsLines()])
            .On("docker", prefix + "up -d --wait", 0,
                " Network commerce_default  Created",
                " Container commerce-sql-1  Healthy",
                " Container commerce-rabbitmq-1  Healthy",
                " Container commerce-keycloak-1  Healthy",
                " Container commerce-catalog-api-1  Healthy",
                " Container commerce-gateway-1  Healthy")
            .On("docker", prefix + "down -v", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Volume commerce_sql-data  Removed",
                " Volume commerce_rabbit-data  Removed",
                " Network commerce_default  Removed")
            .On("docker", prefix + "down", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Network commerce_default  Removed")
            .OnLongRunning("docker", prefix + "logs -f --tail 200", services => FollowLogLines.Where(l => services.Count == 0 || services.Contains(ServiceOf(l))))
            .OnLongRunning("npm", "start", NgServeLines);
    }

    public static IReadOnlyList<string> ComposePsLines()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("compose-ps.jsonl")!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The Compose prefix of a log line: the service name before the <c>|</c>.</summary>
    private static string ServiceOf(string line) => line[..line.IndexOf('|', StringComparison.Ordinal)].Trim();
}
