using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Admin.Host.Workspace;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Workspace;

public sealed partial class WorkspaceStoreTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("admin-workspace-").FullName;
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private WorkspaceStore Store() => new(directory, time);

    private static JsonObject Valid(string widgetId = "w1", string type = "note", JsonObject? config = null) => new()
    {
        ["version"] = WorkspaceSchema.Current,
        ["activePageId"] = "p1",
        ["pages"] = new JsonArray(new JsonObject
        {
            ["id"] = "p1",
            ["title"] = "Mine",
            ["builtIn"] = null,
            ["hidden"] = false,
            ["widgets"] = new JsonArray(new JsonObject
            {
                ["id"] = widgetId,
                ["type"] = type,
                ["x"] = 0,
                ["y"] = 0,
                ["cols"] = 6,
                ["rows"] = 4,
                ["config"] = config ?? new JsonObject { ["text"] = "hello" },
            }),
        }),
    };

    [Fact]
    public void Nothing_saved_reads_as_the_eleven_built_in_pages_as_shipped()
    {
        WorkspaceSnapshot snapshot = Store().Read();

        JsonNode doc = JsonNode.Parse(snapshot.Json)!;
        doc["version"]!.GetValue<int>().ShouldBe(WorkspaceSchema.Current);
        doc["pages"]!.AsArray().Count.ShouldBe(11);
        doc["pages"]!.AsArray().ShouldAllBe(page => page!["widgets"] == null && page["builtIn"] != null);
        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldBeNull();
        snapshot.KeptAside.ShouldBeNull();
        File.Exists(Path.Combine(directory, "workspace.json")).ShouldBeFalse();
    }

    [Fact]
    public void The_default_pages_are_the_spas_screens_in_the_spas_order()
    {
        string root = AppContext.BaseDirectory;

        while (!File.Exists(Path.Combine(root, "BlueprintAdmin.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("BlueprintAdmin.slnx not found above the test output.");
        }

        string source = File.ReadAllText(Path.Combine(root, "src", "Admin.Web", "src", "app", "screens.ts"));
        List<(string Path, string Label)> screens = [.. ScreenRow().Matches(source).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

        WorkspaceDefaults.Screens.ShouldBe(screens);
    }

    [Fact]
    public void The_spas_copies_of_the_schemas_bounds_and_version_are_the_hosts()
    {
        // The SPA saves within these, and a host that moved one alone would refuse every save with a 400.
        string root = AppContext.BaseDirectory;

        while (!File.Exists(Path.Combine(root, "BlueprintAdmin.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("BlueprintAdmin.slnx not found above the test output.");
        }

        string workspace = Path.Combine(root, "src", "Admin.Web", "src", "app", "workspace");
        string types = File.ReadAllText(Path.Combine(workspace, "workspace-types.ts"));
        string builtIn = File.ReadAllText(Path.Combine(workspace, "built-in-pages.ts"));

        SpaConstant(types, "GRID_COLUMNS").ShouldBe(WorkspaceSchema.GridColumns);
        SpaConstant(types, "MAX_ROWS").ShouldBe(WorkspaceSchema.MaxRows);
        SpaConstant(builtIn, "WORKSPACE_VERSION").ShouldBe(WorkspaceSchema.Current);
    }

    private static int SpaConstant(string source, string name)
    {
        Match match = Regex.Match(source, $@"export const {name} = (\d+);");
        match.Success.ShouldBeTrue($"{name} is not an `export const {name} = <number>;` line any more.");

        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void A_saved_workspace_is_read_back_and_its_etag_follows_its_content()
    {
        WorkspaceStore store = Store();
        string first = store.Read().Etag;

        SaveResult saved = store.Save(Valid(), first);

        saved.Outcome.ShouldBe(SaveOutcome.Saved);
        saved.Snapshot!.Etag.ShouldNotBe(first);
        store.Read().ShouldBe(saved.Snapshot);
        new WorkspaceStore(directory, time).Read().Etag.ShouldBe(saved.Snapshot.Etag);
    }

    [Fact]
    public void A_save_based_on_an_old_etag_is_stale_and_changes_nothing()
    {
        WorkspaceStore store = Store();
        string first = store.Read().Etag;
        SaveResult winner = store.Save(Valid("a"), first);

        SaveResult loser = store.Save(Valid("b"), first);

        loser.Outcome.ShouldBe(SaveOutcome.Stale);
        loser.Snapshot.ShouldBe(winner.Snapshot);
        store.Read().ShouldBe(winner.Snapshot);
    }

    [Fact]
    public async Task Of_concurrent_saves_on_one_etag_exactly_one_wins()
    {
        WorkspaceStore store = Store();
        string etag = store.Read().Etag;

        SaveResult[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => store.Save(Valid($"w{i}"), etag), TestContext.Current.CancellationToken)));

        results.Count(r => r.Outcome == SaveOutcome.Saved).ShouldBe(1);
        results.Count(r => r.Outcome == SaveOutcome.Stale).ShouldBe(15);
        Directory.GetFiles(directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void A_write_leaves_no_temp_file_behind()
    {
        WorkspaceStore store = Store();

        store.Save(Valid(), store.Read().Etag).Outcome.ShouldBe(SaveOutcome.Saved);

        Directory.GetFiles(directory).Select(Path.GetFileName).ShouldBe(["workspace.json"]);
    }

    [Fact]
    public void A_replace_needs_no_etag()
    {
        WorkspaceStore store = Store();
        store.Save(Valid("a"), store.Read().Etag);

        SaveResult replaced = store.Replace(Valid("b"));

        replaced.Outcome.ShouldBe(SaveOutcome.Saved);
        store.Read().Json.ShouldContain("\"b\"");
    }

    [Theory]
    [InlineData("not json {")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":3,\"activePageId\":null,\"pages\":[]}")]
    public void A_file_this_console_cannot_use_reads_as_the_default_and_is_kept_aside(string content)
    {
        File.WriteAllText(Path.Combine(directory, "workspace.json"), content);

        WorkspaceSnapshot snapshot = Store().Read();

        JsonNode.Parse(snapshot.Json)!["pages"]!.AsArray().Count.ShouldBe(11);
        snapshot.KeptAside.ShouldNotBeNull();
        File.ReadAllText(snapshot.KeptAside).ShouldBe(content);
        File.Exists(Path.Combine(directory, "workspace.json")).ShouldBeFalse();
    }

    [Fact]
    public void After_a_file_is_kept_aside_the_next_save_starts_from_the_default()
    {
        File.WriteAllText(Path.Combine(directory, "workspace.json"), "garbage");
        WorkspaceStore store = Store();
        string etag = store.Read().Etag;

        store.Save(Valid(), etag).Outcome.ShouldBe(SaveOutcome.Saved);

        Directory.GetFiles(directory, "workspace.unreadable-*").Length.ShouldBe(1);
    }

    [Fact]
    public void An_unknown_widget_type_and_unknown_fields_are_saved_back_untouched()
    {
        JsonObject doc = Valid(type: "from-the-future", config: new JsonObject { ["nested"] = new JsonObject { ["n"] = 1.50, ["list"] = new JsonArray(1, "two") } });
        doc["theme"] = "solar";
        doc["pages"]![0]!["pinned"] = true;
        doc["pages"]![0]!["widgets"]![0]!["z"] = 9;
        WorkspaceStore store = Store();

        store.Save(doc, store.Read().Etag).Outcome.ShouldBe(SaveOutcome.Saved);

        JsonNode.DeepEquals(JsonNode.Parse(Store().Read().Json), doc).ShouldBeTrue();
    }

    [Fact]
    public void An_older_document_is_stepped_up_by_each_migration_on_read()
    {
        JsonObject first = Valid();
        first["version"] = 1;
        File.WriteAllText(Path.Combine(directory, "workspace.json"), first.ToJsonString());
        List<Func<JsonObject, JsonObject>> steps =
        [
            doc => { doc["migratedBy"] = "one"; return doc; },
            doc => { doc["migratedBy"] = $"{doc["migratedBy"]}+two"; return doc; },
        ];
        WorkspaceStore v3 = new(directory, time, version: 3, migrations: steps);

        JsonNode read = JsonNode.Parse(v3.Read().Json)!;

        read["version"]!.GetValue<int>().ShouldBe(3);
        read["migratedBy"]!.GetValue<string>().ShouldBe("one+two");
        File.ReadAllText(Path.Combine(directory, "workspace.json")).ShouldNotContain("migratedBy");
    }

    [Fact]
    public void A_newer_document_than_this_console_knows_is_kept_aside_not_rewritten()
    {
        JsonObject newer = Valid();
        newer["version"] = WorkspaceSchema.Current + 1;
        File.WriteAllText(Path.Combine(directory, "workspace.json"), newer.ToJsonString());

        WorkspaceSnapshot snapshot = Store().Read();

        snapshot.KeptAside.ShouldNotBeNull();
        File.ReadAllText(snapshot.KeptAside).ShouldBe(newer.ToJsonString());
    }

    [Fact]
    public void Each_schema_after_the_first_has_its_migration()
    {
        WorkspaceSchema.Current.ShouldBe(2);
        WorkspaceMigrations.All.Count.ShouldBe(WorkspaceSchema.Current - 1);
    }

    [Fact]
    public void A_phase_1_document_reads_as_version_2_with_its_legacy_screen_widgets_left_for_the_spa()
    {
        // Phase 6 (§5.3): the host knows no widget types, so the step to 2 keeps every page and widget as it was, and
        // the SPA, which owns each screen's defaults, replaces the `legacy` instance it reads.
        JsonObject phase1 = Valid(widgetId: "old", type: "legacy", config: new JsonObject { ["screen"] = "mail" });
        phase1["version"] = 1;
        File.WriteAllText(Path.Combine(directory, "workspace.json"), phase1.ToJsonString());

        WorkspaceSnapshot snapshot = Store().Read();

        JsonNode read = JsonNode.Parse(snapshot.Json)!;
        snapshot.KeptAside.ShouldBeNull();
        read["version"]!.GetValue<int>().ShouldBe(2);
        read["pages"]!.ToJsonString().ShouldBe(phase1["pages"]!.ToJsonString());
        WorkspaceSchema.Validate(read, WorkspaceSchema.Current).ShouldBeNull();
    }

    [Fact]
    public void A_valid_document_has_no_problem()
    {
        WorkspaceSchema.Validate(Valid(), WorkspaceSchema.Current).ShouldBeNull();
    }

    [Theory]
    [InlineData("version", 3)]
    [InlineData("version", "1")]
    public void A_wrong_version_is_refused(string key, object value)
    {
        JsonObject doc = Valid();
        doc[key] = JsonValue.Create(value);

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldNotBeNull();
    }

    [Fact]
    public void A_page_id_used_twice_is_refused()
    {
        JsonObject doc = Valid();
        JsonArray pages = doc["pages"]!.AsArray();
        pages.Add(JsonNode.Parse(pages[0]!.ToJsonString())!.AsObject().Also(p => p["widgets"] = null));

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current)!.ShouldContain("used by another page");
    }

    [Fact]
    public void A_widget_id_used_twice_even_on_another_page_is_refused()
    {
        JsonObject doc = Valid();
        JsonArray pages = doc["pages"]!.AsArray();
        JsonObject second = JsonNode.Parse(pages[0]!.ToJsonString())!.AsObject();
        second["id"] = "p2";
        pages.Add(second);

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current)!.ShouldContain("used by another widget");
    }

    [Theory]
    [InlineData("x", -1)]
    [InlineData("y", -1)]
    [InlineData("cols", 0)]
    [InlineData("cols", 25)]
    [InlineData("rows", 0)]
    [InlineData("rows", 101)]
    public void A_widget_outside_the_grid_is_refused(string cell, int value)
    {
        JsonObject doc = Valid();
        doc["pages"]![0]!["widgets"]![0]![cell] = value;

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldNotBeNull();
    }

    [Fact]
    public void A_widget_whose_right_edge_passes_the_last_column_is_refused()
    {
        JsonObject doc = Valid();
        doc["pages"]![0]!["widgets"]![0]!["x"] = 20;

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldNotBeNull();
    }

    [Fact]
    public void A_widget_touching_the_last_column_is_accepted()
    {
        JsonObject doc = Valid();
        doc["pages"]![0]!["widgets"]![0]!["x"] = 18;

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldBeNull();
    }

    [Theory]
    [InlineData(int.MaxValue, 2)]
    [InlineData(25, 1)]
    public void A_cell_pair_whose_sum_would_overflow_is_refused_not_wrapped(int x, int cols)
    {
        JsonObject doc = Valid();
        doc["pages"]![0]!["widgets"]![0]!["x"] = x;
        doc["pages"]![0]!["widgets"]![0]!["cols"] = cols;

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldNotBeNull();
    }

    [Fact]
    public void Two_unreadable_files_in_one_instant_are_both_kept()
    {
        WorkspaceStore store = Store();
        File.WriteAllText(Path.Combine(directory, "workspace.json"), "first");
        string one = store.Read().KeptAside!;
        File.WriteAllText(Path.Combine(directory, "workspace.json"), "second");
        string two = store.Read().KeptAside!;

        two.ShouldNotBe(one);
        File.ReadAllText(one).ShouldBe("first");
        File.ReadAllText(two).ShouldBe("second");
    }

    [Theory]
    [InlineData("x", "3")]
    [InlineData("cols", 1.5)]
    public void A_cell_that_is_not_an_integer_is_refused(string cell, object value)
    {
        JsonObject doc = Valid();
        doc["pages"]![0]!["widgets"]![0]![cell] = JsonValue.Create(value);

        WorkspaceSchema.Validate(doc, WorkspaceSchema.Current).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Token")]
    [InlineData("clientSecret")]
    [InlineData("AUTHORIZATION")]
    public void A_secret_named_config_key_is_refused_at_any_depth(string key)
    {
        JsonObject top = Valid(config: new JsonObject { [key] = "x" });
        JsonObject nested = Valid(config: new JsonObject { ["a"] = new JsonArray(new JsonObject { [key] = "x" }) });

        WorkspaceSchema.Validate(top, WorkspaceSchema.Current)!.ShouldContain("named like a secret");
        WorkspaceSchema.Validate(nested, WorkspaceSchema.Current)!.ShouldContain("named like a secret");
    }

    [Fact]
    public void A_secret_word_in_a_value_is_not_a_secret_key()
    {
        WorkspaceSchema.Validate(Valid(config: new JsonObject { ["text"] = "the password is in the vault" }), WorkspaceSchema.Current).ShouldBeNull();
    }

    [Fact]
    public void A_config_past_its_limit_is_refused()
    {
        JsonObject big = Valid(config: new JsonObject { ["text"] = new string('x', WorkspaceSchema.MaxConfigBytes) });

        WorkspaceSchema.Validate(big, WorkspaceSchema.Current)!.ShouldContain("config is larger");
    }

    [Fact]
    public void A_refused_save_writes_nothing()
    {
        WorkspaceStore store = Store();
        JsonObject bad = Valid();
        bad["pages"]![0]!["widgets"]![0]!["cols"] = 99;

        SaveResult result = store.Save(bad, store.Read().Etag);

        result.Outcome.ShouldBe(SaveOutcome.Invalid);
        result.Problem.ShouldNotBeNull();
        Directory.Exists(directory).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "workspace.json")).ShouldBeFalse();
    }

    [GeneratedRegex(@"path: '([a-z]+)',\s+label: '([^']+)',")]
    private static partial Regex ScreenRow();
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);

        return node;
    }
}
