using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/operations", async (ApiCatalog catalog, CancellationToken cancellationToken) =>
            TypedResults.Ok(await catalog.GetAsync(cancellationToken)));

        app.MapPost("/api/catalog/reload", async (ApiCatalog catalog, CancellationToken cancellationToken) =>
            TypedResults.Ok(await catalog.ReloadAsync(cancellationToken)));

        app.MapPost("/api/proxy", async Task<Results<Ok<ProxyResult>, ProblemHttpResult>> (ProxyRequest request, RequestProxy proxy, CancellationToken cancellationToken) =>
            proxy.Validate(request) is string problem
                ? TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request not sent", detail: problem)
                : TypedResults.Ok(await proxy.SendAsync(request, cancellationToken)));

        return app;
    }
}
