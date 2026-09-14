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

    private sealed class DelayedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private static PlatformProbe Probe(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new ScriptedHandler(respond)), Options.Create(new AdminOptions()));

    private static PlatformProbe DelayedProbe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new DelayedHandler(respond)), Options.Create(new AdminOptions()));

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

    [Fact]
    public async Task A_target_that_never_answers_is_reported_down()
    {
        PlatformProbe probe = DelayedProbe(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Port == 3000)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        IReadOnlyList<Reachability> result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Single(r => r.Name == "grafana").ShouldBe(new Reachability("grafana", "http://localhost:3000/api/health", false, null));
        result.Single(r => r.Name == "keycloak").Up.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_down()
    {
        PlatformProbe probe = DelayedProbe(async (request, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => probe.ProbeAsync(cts.Token));
    }
}
