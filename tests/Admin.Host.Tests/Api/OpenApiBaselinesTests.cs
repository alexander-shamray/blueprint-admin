using System.Text.Json;
using Admin.Host.Api;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class OpenApiBaselinesTests : IDisposable
{
    private const string V1 = """
        {
          "paths": { "/v1/orders/": { "post": { "responses": { "200": {}, "422": {} } }, "summary": "not an operation" } },
          "components": { "schemas": { "Order": { "properties": { "id": {}, "status": {} } } } }
        }
        """;

    private const string V2 = """
        {
          "paths": {
            "/v1/orders/": { "post": { "responses": { "200": {}, "409": {} } } },
            "/v1/orders/{id}/cancel": { "post": { "responses": { "204": {} } } }
          },
          "components": { "schemas": { "Order": { "properties": { "id": {}, "buyerStatus": {} } } } }
        }
        """;

    private readonly string directory = Directory.CreateTempSubdirectory("admin-baselines-").FullName;
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void The_first_sight_of_a_service_takes_its_baseline_and_reports_nothing_moved()
    {
        OpenApiChanges changes = new OpenApiBaselines(directory, time).Compare("ordering", V1);

        changes.BaselineTaken.ShouldBeTrue();
        changes.BaselineAt.ShouldBe(time.GetUtcNow());
        changes.Any.ShouldBeFalse();
        File.Exists(Path.Combine(directory, "openapi-ordering.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_later_document_is_read_against_the_baseline_operations_statuses_and_fields()
    {
        OpenApiBaselines baselines = new(directory, time);
        DateTimeOffset takenAt = time.GetUtcNow();
        baselines.Compare("ordering", V1);
        time.Advance(TimeSpan.FromDays(1));

        OpenApiChanges changes = baselines.Compare("ordering", V2);

        changes.BaselineTaken.ShouldBeFalse();
        changes.BaselineAt.ShouldBe(takenAt);
        changes.OperationsAdded.ShouldBe(["POST /v1/orders/{id}/cancel"]);
        changes.OperationsRemoved.ShouldBeEmpty();
        changes.StatusesAdded.ShouldBe(["POST /v1/orders/ 409", "POST /v1/orders/{id}/cancel 204"]);
        changes.StatusesRemoved.ShouldBe(["POST /v1/orders/ 422"]);
        changes.FieldsAdded.ShouldBe(["Order.buyerStatus"]);
        changes.FieldsRemoved.ShouldBe(["Order.status"]);
    }

    [Fact]
    public void The_baseline_stays_until_accepted_and_then_compares_clean()
    {
        OpenApiBaselines baselines = new(directory, time);
        baselines.Compare("ordering", V1);

        baselines.Compare("ordering", V2).Any.ShouldBeTrue();
        baselines.Compare("ordering", V2).Any.ShouldBeTrue();

        baselines.Accept("ordering", V2);

        baselines.Compare("ordering", V2).Any.ShouldBeFalse();
    }

    [Fact]
    public void A_baseline_file_it_did_not_write_is_reported_rather_than_compared()
    {
        File.WriteAllText(Path.Combine(directory, "openapi-ordering.json"), """{"something":"else"}""");

        OpenApiChanges changes = new OpenApiBaselines(directory, time).Compare("ordering", V1);

        changes.Error.ShouldNotBeNull();
        changes.Error.ShouldContain("is not a baseline this console wrote");
    }

    [Fact]
    public void The_shape_ignores_path_item_keys_that_are_not_methods()
    {
        using JsonDocument document = JsonDocument.Parse(V1);

        OpenApiShape.Of(document.RootElement).Operations.ShouldBe(["POST /v1/orders/"]);
    }
}
