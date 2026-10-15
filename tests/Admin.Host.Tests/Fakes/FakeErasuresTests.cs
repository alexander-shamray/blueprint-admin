using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.Identity;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>The fake Privacy behind the gateway: who may reach it, how a request moves, and how the overdue one is set right.</summary>
public sealed class FakeErasuresTests
{
    private const string Gateway = "http://gateway.test";

    private const string Routes = "/api/v1/privacy/erasure-requests";

    private readonly FakeOrders orders = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));

    private static string Holder => TokenServiceTests.Jwt("""{"permission":["privacy:erase"],"preferred_username":"privacy"}""");

    private HttpResponseMessage Call(string method, string path, string? body = null, string? token = null)
    {
        using HttpRequestMessage request = new(new HttpMethod(method), Gateway + path);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Holder);

        return FakeGateway.Send(request, orders);
    }

    private async Task<Guid> RaiseAsync(Guid subject)
    {
        using HttpResponseMessage raised = Call("POST", Routes, $$"""{"subjectId":"{{subject}}"}""");
        raised.StatusCode.ShouldBe(HttpStatusCode.OK);

        return Guid.Parse(JsonSerializer.Deserialize<string>(await raised.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
    }

    private async Task<JsonElement> ReadAsync(Guid id)
    {
        using HttpResponseMessage read = Call("GET", $"{Routes}/{id}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);

        return JsonDocument.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
    }

    private static string[] Names(JsonElement array) => [.. array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetProperty("responder").GetString()!)];

    [Fact]
    public void A_login_without_the_permission_is_403_and_demo_is_such_a_login()
    {
        string demo = TokenServiceTests.Jwt("""{"permission":["catalog:write","orders:write","orders:cancel","inventory:admin","payments:admin"]}""");

        Call("POST", Routes, $$"""{"subjectId":"{{Guid.NewGuid()}}"}""", demo).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_subject_that_answers_reads_open_with_three_holders_then_closed_with_all_five()
    {
        Guid id = await RaiseAsync(Guid.NewGuid());

        JsonElement first = await ReadAsync(id);
        first.GetProperty("status").GetString().ShouldBe("Open");
        Names(first.GetProperty("answers")).ShouldBe(["ordering", "payments", "shipping"]);
        Names(first.GetProperty("missing")).ShouldBe(["notifications", "bff"]);
        first.GetProperty("closedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        JsonElement second = await ReadAsync(id);
        second.GetProperty("status").GetString().ShouldBe("Closed");
        Names(second.GetProperty("answers")).ShouldBe(["ordering", "payments", "shipping", "notifications", "bff"]);
        second.GetProperty("missing").GetArrayLength().ShouldBe(0);
        second.GetProperty("requestId").GetGuid().ShouldBe(id);
    }

    [Fact]
    public async Task Asking_again_for_a_subject_with_a_request_open_returns_that_request()
    {
        Guid subject = Guid.NewGuid();
        Guid id = await RaiseAsync(subject);

        (await RaiseAsync(subject)).ShouldBe(id);

        await ReadAsync(id);
        await ReadAsync(id);
        (await RaiseAsync(subject)).ShouldNotBe(id);
    }

    [Fact]
    public async Task The_overdue_subject_stays_overdue_until_a_reissue_and_then_closes()
    {
        Guid id = await RaiseAsync(FakeErasures.OverdueSubject);

        await ReadAsync(id);
        JsonElement stuck = await ReadAsync(id);
        stuck.GetProperty("status").GetString().ShouldBe("Overdue");
        Names(stuck.GetProperty("missing")).ShouldBe(["notifications", "bff"]);
        stuck.GetProperty("reissues").GetInt32().ShouldBe(0);

        using HttpResponseMessage reissued = Call("POST", $"{Routes}/{id}/reissue", $$"""{"commandId":"{{Guid.NewGuid()}}"}""");
        reissued.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        JsonElement closed = await ReadAsync(id);
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("reissues").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_reissue_of_a_closed_request_is_the_422_and_of_an_unknown_one_the_404()
    {
        Guid id = await RaiseAsync(Guid.NewGuid());
        await ReadAsync(id);
        await ReadAsync(id);

        using HttpResponseMessage closed = Call("POST", $"{Routes}/{id}/reissue", $$"""{"commandId":"{{Guid.NewGuid()}}"}""");
        closed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await closed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("erasure_request.closed");

        using HttpResponseMessage unknown = Call("POST", $"{Routes}/{Guid.NewGuid()}/reissue", $$"""{"commandId":"{{Guid.NewGuid()}}"}""");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("erasure_request.not_found");
        Call("GET", $"{Routes}/{Guid.NewGuid()}").StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"subjectId":"not-a-guid"}""")]
    [InlineData("""{"subjectId":"00000000-0000-0000-0000-000000000000"}""")]
    public void A_subject_that_is_empty_or_no_guid_is_the_validators_400(string body) =>
        Call("POST", Routes, body).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
}
