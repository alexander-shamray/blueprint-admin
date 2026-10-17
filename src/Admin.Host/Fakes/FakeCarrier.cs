using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Admin.Host.Fakes;

/// <summary>
/// The carrier simulator's <c>POST /__admin/requests/find</c> (run-locally.md): WireMock.Net's own request model in, the
/// matching log entries out. Only the two filters the Scenario sends are understood: the bookings whose body names a code
/// (<c>Body.Matchers[0].Pattern</c> of <c>*SIM-X*</c>) and the feed polls of one order's parcel (<c>Path.Matchers[0].Pattern</c> of
/// <c>/v1/shipments/crr_SIM-X-&lt;orderId&gt;/events</c>; the bare <c>crr_SIM-X</c> path the simulator still serves for an earlier
/// booking is read as every order of the code). A second body matcher, the Scenario's mark of its own run, is not read: the fake's
/// log holds the orders to the code and none of them carries a mark. Anything else finds nothing, as an unmatched filter does on the simulator.
/// </summary>
internal static partial class FakeCarrier
{
    public static HttpResponseMessage Find(HttpRequestMessage request, FakeOrders orders)
    {
        JsonElement? body = Body(request);
        string method = body?.Get("Methods") is { ValueKind: JsonValueKind.Array } methods && methods.GetArrayLength() > 0 ? methods[0].GetString() ?? "" : "";
        string path = Pattern(body, "Path");
        string content = Pattern(body, "Body");

        if (method == "GET" && FeedPath().Match(path) is { Success: true } feed)
        {
            return FakeHttp.Json(HttpStatusCode.OK, orders.CarrierLog(feed.Groups["code"].Value, polls: true, feed.Groups["order"] is { Success: true } order ? order.Value : null));
        }

        if (method == "POST" && path == "/v1/shipments" && Code().Match(content) is { Success: true } booking)
        {
            return FakeHttp.Json(HttpStatusCode.OK, orders.CarrierLog(booking.Groups["code"].Value, polls: false));
        }

        return FakeHttp.Json(HttpStatusCode.OK, "[]");
    }

    private static string Pattern(JsonElement? body, string member) =>
        body?.Get(member)?.Get("Matchers") is { ValueKind: JsonValueKind.Array } matchers && matchers.GetArrayLength() > 0
            && matchers[0].Get("Pattern") is { ValueKind: JsonValueKind.String } pattern
            ? pattern.GetString() ?? ""
            : "";

    private static JsonElement? Body(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : null;

    [GeneratedRegex(@"^/v1/shipments/crr_(?<code>SIM-[A-Z]+)(-(?<order>[^/]+))?/events$")]
    private static partial Regex FeedPath();

    [GeneratedRegex(@"^\*(?<code>SIM-[A-Z]+)\*$")]
    private static partial Regex Code();
}
