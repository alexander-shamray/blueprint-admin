using System.Net;
using System.Net.Mime;
using System.Text;

namespace Admin.Host.Fakes;

/// <summary>Every workstation surface answers 200 in FakePlatform mode. Later phases add the Keycloak, OpenAPI and Grafana bodies here.</summary>
public sealed class FakePlatformHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => """{"status":"Healthy"}""",
            "/api/health" => """{"database":"ok","version":"fake"}""",
            _ => """{"fake":true}""",
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
        });
    }
}
