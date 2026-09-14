using System.Net;
using Admin.Host.Config;
using Admin.Host.Stack;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class PlatformProbeTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static PlatformProbe Probe(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new ScriptedHandler(respond)), Options.Create(new AdminOptions()));

    [Fact]
    public async Task Reports_each_surface_in_order_with_its_status()
    {
        PlatformProbe probe = Probe(request => request.RequestUri!.Port == 3000
            ? throw new HttpRequestException("refused")
            : new HttpResponseMessage(request.RequestUri.Port == 8080 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));

        IReadOnlyList<Reachability> result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Select(r => r.Name).ShouldBe(["gateway", "catalog", "ordering", "bff", "keycloak", "grafana", "client"]);
        result.Single(r => r.Name == "gateway").Url.ShouldBe("http://localhost:5000/health/ready");
        result.Single(r => r.Name == "keycloak").ShouldBe(new Reachability("keycloak", "http://localhost:8080/realms/commerce", true, 200));
        result.Single(r => r.Name == "grafana").ShouldBe(new Reachability("grafana", "http://localhost:3000/api/health", false, null));
        result.Single(r => r.Name == "gateway").Up.ShouldBeFalse();
        result.Single(r => r.Name == "gateway").Status.ShouldBe(503);
    }
}
