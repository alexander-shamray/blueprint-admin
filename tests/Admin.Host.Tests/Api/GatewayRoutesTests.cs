using Admin.Host.Api;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class GatewayRoutesTests
{
    [Theory]
    [InlineData("GET", "/api/v1/catalog/products/", "anonymous")]
    [InlineData("GET", "/api/v1/catalog/products", "anonymous")]
    [InlineData("POST", "/api/v1/catalog/products/", "authenticated")]
    [InlineData("PUT", "/api/v1/catalog/products/", GatewayRoutes.NoRoute)]
    [InlineData("POST", "/api/v1/orders/", "authenticated")]
    [InlineData("DELETE", "/api/v1/orders/{id}/cancel", "authenticated")]
    [InlineData("GET", "/api/v1/inventory/items", "inventory:admin")]
    [InlineData("POST", "/bff/v1/checkout/quote", "authenticated")]
    [InlineData("GET", "/api/v1/unknown", GatewayRoutes.NoRoute)]
    [InlineData("GET", "/api/v1/catalogue", GatewayRoutes.NoRoute)]
    public void Each_method_and_path_gets_the_policy_of_the_route_that_matches(string method, string path, string policy)
    {
        GatewayRoutes.PolicyFor(method, path).ShouldBe(policy);
    }
}
