using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Fakes;

/// <summary>
/// The platform client's recording seam: a successful answer to a request <see cref="FixtureRecordings.Http"/>
/// names is buffered, handed to the recorder and returned to its caller unchanged. With recording off, or for any
/// other request, it passes the answer straight through.
/// </summary>
public sealed class RecordingHandler(FixtureRecorder recorder, IOptions<AdminOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (!recorder.On || !response.IsSuccessStatusCode || request.RequestUri is not { } uri
            || FixtureRecordings.For(options.Value, uri) is not { } recording)
        {
            return response;
        }

        string? key = recording.KeyOf?.Invoke(uri);

        if (recording.KeyOf is not null && key is null)
        {
            return response;
        }

        // Buffered, so the caller still reads the whole body after this has.
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (key is null)
        {
            recorder.Write(recording.Fixture, body);
        }
        else
        {
            recorder.WriteKeyed(recording.Fixture, key, body);
        }

        return response;
    }
}
