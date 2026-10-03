using System.Text.Json;
using Admin.Host.Api;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The gateway's <c>ReverseProxy:Routes</c> against <see cref="GatewayRoutes"/>, and the BFF quote
/// route against <see cref="CuratedOperations"/>.
/// </summary>
public sealed class GatewayDriftTests
{
    /// <summary>The methods a route with no <c>Methods</c> is asked about; it matches all of them.</summary>
    private static readonly string[] AnyMethod = ["GET", "POST", "PUT", "DELETE"];

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [Fact]
    public void Every_gateway_route_has_its_policy_in_GatewayRoutes()
    {
        using JsonDocument settings = JsonDocument.Parse(Backend.Read("src", "Gateway", "Gateway.Api", "appsettings.json"), Jsonc);
        JsonElement routes = settings.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");
        List<string> drifted = [];

        foreach (JsonProperty route in routes.EnumerateObject())
        {
            JsonElement match = route.Value.GetProperty("Match");
            string path = match.GetProperty("Path").GetString()!;
            string prefix = path[..path.IndexOf('{', StringComparison.Ordinal)];
            string policy = route.Value.GetProperty("AuthorizationPolicy").GetString()!;
            string[] methods = match.TryGetProperty("Methods", out JsonElement listed)
                ? [.. listed.EnumerateArray().Select(m => m.GetString()!)]
                : AnyMethod;

            foreach (string method in methods)
            {
                string copied = GatewayRoutes.PolicyFor(method, prefix);

                if (copied != policy)
                {
                    drifted.Add($"{route.Name}: {method} {prefix} is '{policy}' at the gateway and '{copied}' here");
                }
            }
        }

        drifted.ShouldBeEmpty();
    }

    [Fact]
    public void The_quote_the_console_curates_is_still_mapped_by_the_bff()
    {
        string endpoints = Backend.Read("src", "BFF", "Web.Bff", "Endpoints", "CheckoutEndpoints.cs");

        endpoints.ShouldContain("""MapGroup("/v1/checkout")""");
        endpoints.ShouldContain("\"/quote\"");
    }

    [Fact]
    public void Every_host_still_maps_the_readiness_path_the_console_calls()
    {
        Backend.Read("src", "BuildingBlocks", "Common.Web", "HealthCheckExtensions.cs").ShouldContain("""MapHealthChecks("/health/ready""");
    }
}
