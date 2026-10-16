using System.Net;
using System.Text;
using Admin.Host.Config;
using Admin.Host.Scenario;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Scenario;

public sealed class ReviewFlagServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid OrderId = Guid.Parse("01a1446b-ca9a-7c8c-ae31-5fc6a8239f13");

    private const string Datasources = """
        [{"uid":"loki","type":"loki"},{"uid":"prometheus","type":"prometheus"},{"uid":"tempo","type":"tempo"}]
        """;

    private readonly FakeTimeProvider time = new(Now);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>One Loki stream per line, shaped as the Trace screen's fixtures are: a nanosecond string and the formatted message.</summary>
    private static string Streams(params (DateTimeOffset At, string Message)[] lines)
    {
        string streams = string.Join(",", lines.Select(line =>
            $$$"""{"stream":{"service_name":"Ordering.Api"},"values":[["{{{line.At.ToUnixTimeMilliseconds() * 1_000_000L}}}","{{{line.Message}}}"]]}"""));

        return $$$"""{"status":"success","data":{"resultType":"streams","result":[{{{streams}}}]}}""";
    }

    private ReviewFlagService Service(ScriptedHandler handler) =>
        new(new GrafanaClient(new HttpClient(handler), Options.Create(new AdminOptions())), time);

    private static ScriptedHandler Loki(string answer) =>
        new(request => request.RequestUri!.AbsolutePath == "/api/datasources" ? Json(Datasources) : Json(answer));

    [Fact]
    public void The_LogQL_is_the_query_run_locally_names_for_the_flag()
    {
        ReviewFlagService.LogQl("abc").ShouldBe(""""
            {service_name="Ordering.Api"} |= "flagged for review" |= "abc"
            """");
    }

    [Fact]
    public async Task A_flag_line_is_read_as_the_reason_after_the_colon_and_the_line_itself()
    {
        string line = $"Order {OrderId} flagged for review: not_despatched.";
        ReviewFlagService service = Service(Loki(Streams((Now.AddMinutes(-1), line))));

        ReviewFlagView view = await service.ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        view.Reachable.ShouldBeTrue();
        view.Error.ShouldBeNull();
        view.OrderId.ShouldBe(OrderId.ToString("D"));
        view.Window.ShouldBe("30m");
        ReviewFlag flag = view.Flags.ShouldHaveSingleItem();
        flag.Reason.ShouldBe("not_despatched");
        flag.Line.ShouldBe(line);
        flag.At.ShouldBe(Now.AddMinutes(-1));
    }

    [Fact]
    public async Task A_line_that_is_not_this_orders_flag_is_ignored()
    {
        ReviewFlagService service = Service(Loki(Streams(
            (Now.AddMinutes(-3), $"Order {OrderId} placed."),
            (Now.AddMinutes(-2), "Order 00000000-0000-0000-0000-000000000001 flagged for review: stock_not_released."),
            (Now.AddMinutes(-1), $"Order {OrderId} flagged for review: stock_not_released."))));

        ReviewFlagView view = await service.ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        view.Flags.Select(f => f.Reason).ShouldBe(["stock_not_released"]);
    }

    [Fact]
    public async Task Flags_come_back_oldest_first_whatever_order_Loki_answers_its_streams_in()
    {
        ReviewFlagService service = Service(Loki(Streams(
            (Now.AddMinutes(-1), $"Order {OrderId} flagged for review: not_despatched."),
            (Now.AddMinutes(-5), $"Order {OrderId} flagged for review: stock_not_released."))));

        ReviewFlagView view = await service.ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        view.Flags.Select(f => f.Reason).ShouldBe(["stock_not_released", "not_despatched"]);
    }

    [Fact]
    public async Task An_order_with_no_line_has_no_flags_and_Loki_was_reachable()
    {
        ReviewFlagService service = Service(Loki(Streams()));

        ReviewFlagView view = await service.ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        view.Reachable.ShouldBeTrue();
        view.Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_Loki_is_said_and_never_read_as_no_flags()
    {
        ReviewFlagService service = Service(new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/datasources"
                ? Json(Datasources)
                : Json("""{"message":"boom"}""", HttpStatusCode.BadGateway)));

        ReviewFlagView view = await service.ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("502");
        view.Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_query_asks_for_this_order_over_the_window_ending_now()
    {
        ScriptedHandler handler = Loki(Streams());

        await Service(handler).ReadAsync(OrderId, TimeSpan.FromMinutes(30), Token);

        Uri query = handler.Requests.Last().Request.RequestUri!;
        string decoded = Uri.UnescapeDataString(query.Query);
        decoded.ShouldContain(ReviewFlagService.LogQl(OrderId.ToString("D")));
        decoded.ShouldContain($"start={Now.AddMinutes(-30).ToUnixTimeMilliseconds() * 1_000_000L}");
        decoded.ShouldContain($"end={Now.ToUnixTimeMilliseconds() * 1_000_000L}");
        decoded.ShouldContain($"limit={ReviewFlagService.MaxLines}");
    }
}
