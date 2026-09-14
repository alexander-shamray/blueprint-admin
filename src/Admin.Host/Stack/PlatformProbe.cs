using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Stack;

public sealed record Reachability(string Name, string Url, bool Up, int? Status);

/// <summary>
/// Whether each HTTP surface on the workstation answers. A refused connection
/// is <c>Up = false</c> with no status; a 503 from a readiness check is
/// <c>Up = false</c> with the status, because the two mean different things
/// on the Stack screen.
/// </summary>
public sealed class PlatformProbe(HttpClient http, IOptions<AdminOptions> options)
{
    private static readonly TimeSpan PerTarget = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<Reachability>> ProbeAsync(CancellationToken cancellationToken)
    {
        AdminOptions o = options.Value;

        (string Name, string Url)[] targets =
        [
            ("gateway", $"{o.GatewayUrl}/health/ready"),
            ("catalog", $"{o.CatalogUrl}/health/ready"),
            ("ordering", $"{o.OrderingUrl}/health/ready"),
            ("bff", $"{o.BffUrl}/health/ready"),
            ("keycloak", $"{o.KeycloakUrl}/realms/{o.Realm}"),
            ("grafana", $"{o.GrafanaUrl}/api/health"),
            ("client", $"{o.ClientUrl}/"),
        ];

        return await Task.WhenAll(targets.Select(t => ProbeOneAsync(t.Name, t.Url, cancellationToken)));
    }

    private async Task<Reachability> ProbeOneAsync(string name, string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerTarget);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return new Reachability(name, url, response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token fired from CancelAfter, not from the caller: a
            // genuine per-target timeout, reported as down. If the caller's own
            // token is what cancelled, let it propagate as cancellation instead.
            return new Reachability(name, url, false, null);
        }
        catch (HttpRequestException)
        {
            return new Reachability(name, url, false, null);
        }
    }
}
