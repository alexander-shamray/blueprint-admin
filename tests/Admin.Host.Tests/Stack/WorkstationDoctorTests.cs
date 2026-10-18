using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Stack;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class WorkstationDoctorTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private const string Model = """
        {"name":"commerce","services":{
          "gateway":{"ports":[{"target":8080,"published":"5000"}],"environment":{"Cors__Enabled":"true","Cors__Origins__0":"http://localhost:5173","Cors__Origins__1":"https://localhost"}},
          "rabbitmq":{"ports":[{"target":5672,"published":"5672"},{"target":15672,"published":"15672"}]},
          "catalog-migrator":{}
        }}
        """;

    private static CommandOutput Said(params string[] lines) => CommandOutput.Answered(lines);

    /// <summary>The stack of <see cref="Model"/> running: the gateway and RabbitMQ publish every port it declares.</summary>
    private static readonly ComposeStatus Running = new(true, null,
    [
        new ServiceStatus("gateway", "running", "healthy", 0, [5000]),
        new ServiceStatus("rabbitmq", "running", "healthy", 0, [5672, 15672]),
    ]);

    private static readonly ComposeStatus Down = new(true, null, [new ServiceStatus("gateway", "exited", null, 0, [])]);

    private static CommandOutput Failed(string error) => CommandOutput.Failed(error);

    private static string Listener(int port, int pid, string? process) =>
        $$"""{"LocalPort":{{port}},"OwningProcess":{{pid}},"Process":{{(process is null ? "null" : $"\"{process}\"")}}}""";

    private static DoctorCheck Ports(params string[] listeners)
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        return WorkstationDoctor.Ports(model, Said(Model), Said($"[{string.Join(',', listeners)}]"), Running);
    }

    [Fact]
    public void A_published_port_held_by_another_program_is_a_problem_that_names_it()
    {
        DoctorCheck check = Ports(Listener(5000, 36668, "com.docker.backend"), Listener(5672, 7376, "erl"), Listener(15672, 7376, "erl"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376).");
    }

    [Fact]
    public void Ports_held_by_docker_or_free_are_fine_and_unpublished_listeners_are_ignored()
    {
        DoctorCheck check = Ports(Listener(5000, 36668, "com.docker.backend"), Listener(61065, 24320, "sqlservr"));

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldBe("1 of 3 published ports are held by this stack's running containers, the rest are free.");
    }

    [Fact]
    public void Every_published_port_held_by_docker_is_the_stack_running()
    {
        DoctorCheck check = Ports(Listener(5000, 36668, "com.docker.backend"), Listener(5672, 36668, "com.docker.backend"), Listener(15672, 36668, "com.docker.backend"));

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldBe("All 3 published ports are held by this stack's running containers.");
    }

    [Fact]
    public void One_listener_is_printed_as_an_object_not_an_array_and_still_reads()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Said(Listener(5672, 7376, null)), Running);

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("5672 by an unnamed process (pid 7376)");
    }

    [Fact]
    public void No_listeners_at_all_is_every_port_free()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        WorkstationDoctor.Ports(model, Said(Model), Said(), Down).Detail.ShouldBe("All 3 published ports are free.");
    }

    [Fact]
    public void A_listener_read_that_does_not_answer_is_unknown_not_fine()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Failed("powershell: not found"), Running);

        check.State.ShouldBe(DoctorState.Unknown);
        check.Detail.ShouldContain("powershell: not found");
    }

    [Fact]
    public void Without_the_compose_model_neither_ports_nor_cors_can_be_judged()
    {
        WorkstationDoctor.Ports(null, Failed("no such file"), Said(), Running).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Cors(null, Failed("no such file"), "http://localhost:5173").State.ShouldBe(DoctorState.Unknown);
    }

    [Theory]
    [InlineData("http://localhost:5173", DoctorState.Ok)]
    [InlineData("http://localhost:4200", DoctorState.Problem)]
    public void The_cors_row_asks_whether_the_gateway_admits_the_consoles_client_origin(string clientUrl, DoctorState expected)
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Cors(model, Said(Model), clientUrl);

        check.State.ShouldBe(expected);
        check.Detail.ShouldContain("http://localhost:5173, https://localhost");
    }

    [Theory]
    [InlineData("v22.23.2", "22.23.2", DoctorState.Ok)]
    [InlineData("v22.23.2", "22", DoctorState.Ok)]
    [InlineData("v22.23.2", "v22.23", DoctorState.Ok)]
    [InlineData("v22.23.2", "22.2", DoctorState.Problem)]
    [InlineData("v20.11.0", "22.23.2", DoctorState.Problem)]
    public void Node_matches_the_pin_whole_or_by_its_leading_parts(string running, string pinned, DoctorState expected) =>
        WorkstationDoctor.Node(Said(running), Said(pinned)).State.ShouldBe(expected);

    [Fact]
    public void A_missing_node_is_a_problem_and_a_missing_pin_is_unknown()
    {
        WorkstationDoctor.Node(Failed("node: not found"), Said("22")).State.ShouldBe(DoctorState.Problem);
        WorkstationDoctor.Node(Said("v22.23.2"), Failed("cannot find path")).State.ShouldBe(DoctorState.Unknown);
    }

    [Theory]
    [InlineData("Python 3.12.7", DoctorState.Ok)]
    [InlineData("Python 3.12.0", DoctorState.Ok)]
    [InlineData("Python 3.13.1", DoctorState.Problem)]
    [InlineData("Python 3.120.1", DoctorState.Problem)]
    [InlineData("Python 2.7.18", DoctorState.Problem)]
    [InlineData("", DoctorState.Problem)]
    public void Python_is_ok_only_for_a_3_12_answer(string answer, DoctorState expected) =>
        WorkstationDoctor.Python(Said(answer)).State.ShouldBe(expected);

    [Fact]
    public void A_py_that_does_not_start_is_a_problem_naming_what_needs_it()
    {
        DoctorCheck check = WorkstationDoctor.Python(Failed("No suitable Python runtime found"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("No suitable Python runtime found");
        check.Detail.ShouldContain("dead-letter");
    }

    [Fact]
    public void K6_answering_is_ok_and_names_its_version()
    {
        DoctorCheck check = WorkstationDoctor.K6(Said("k6.exe v2.2.0 (commit/00a9a1b7f5, go1.26.5, windows/amd64)"));

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldStartWith("k6.exe v2.2.0");
    }

    [Fact]
    public void A_missing_k6_is_a_problem_in_the_reads_own_words_which_name_the_install()
    {
        DoctorCheck check = WorkstationDoctor.K6(Said("k6: not installed (winget install k6 --source winget)"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("k6: not installed (winget install k6 --source winget); the load run needs it.");
    }

    [Fact]
    public void A_k6_read_that_did_not_run_cannot_tell_and_one_that_said_nothing_is_a_problem()
    {
        WorkstationDoctor.K6(Failed("powershell: not found")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.K6(Said("")).State.ShouldBe(DoctorState.Problem);
    }

    [Theory]
    [InlineData("## main...origin/main", DoctorState.Ok, "On main, level with origin/main as last fetched.")]
    [InlineData("## main...origin/main [ahead 1]", DoctorState.Ok, "On main, level with origin/main as last fetched (ahead 1).")]
    [InlineData("## main...origin/main [behind 3]", DoctorState.Problem, "main is 3 behind its upstream as last fetched.")]
    [InlineData("## main...origin/main [ahead 1, behind 2]", DoctorState.Problem, "main is 2 behind its upstream as last fetched.")]
    [InlineData("## feat/x...origin/feat/x", DoctorState.Problem, "On feat/x, not main.")]
    [InlineData("## HEAD (no branch)", DoctorState.Problem, "On HEAD, not main.")]
    [InlineData("## main", DoctorState.Ok, "On main, with no upstream as last fetched.")]
    public void A_clone_is_judged_by_its_branch_and_its_last_fetch(string line, DoctorState expected, string detail)
    {
        DoctorCheck check = WorkstationDoctor.Clone("Backend clone", Said(line));

        check.State.ShouldBe(expected);
        check.Detail.ShouldBe(detail);
    }

    [Fact]
    public void A_clone_with_changes_says_so_beside_its_verdict()
    {
        WorkstationDoctor.Clone("Backend clone", Said("## main...origin/main", " M README.md")).Detail.ShouldEndWith(" It has uncommitted changes.");
    }

    [Fact]
    public void Only_images_compose_built_are_compared_and_those_before_the_last_commit_are_named()
    {
        CommandOutput images = Said("""
            [{"ContainerName":"commerce-web-bff-1","Repository":"commerce-web-bff","Created":"2026-10-03T07:18:50Z"},
             {"ContainerName":"commerce-catalog-api-1","Repository":"commerce-catalog-api","Created":"2026-10-04T09:00:00Z"},
             {"ContainerName":"commerce-rabbitmq-1","Repository":"rabbitmq","Created":"2025-01-01T00:00:00Z"}]
            """);

        DoctorCheck check = WorkstationDoctor.Images(images, Said("HEAD@{2026-10-04T02:19:22+05:00}"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("1 of 2 built images were tagged before the backend's checkout last moved (2026-10-04 02:19 +05:00), and Up does not rebuild them: commerce-web-bff.");
    }

    [Fact]
    public void No_built_image_running_is_nothing_to_compare_rather_than_fine()
    {
        WorkstationDoctor.Images(Said("[]"), Said("HEAD@{2026-10-04T02:19:22+05:00}")).State.ShouldBe(DoctorState.Unknown);
    }

    [Fact]
    public void An_image_read_that_does_not_answer_or_cannot_be_read_is_unknown_never_fine()
    {
        const string commit = "HEAD@{2026-10-04T02:19:22+05:00}";

        WorkstationDoctor.Images(Failed("compose: not found"), Said(commit)).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Images(Said("""[{"Repository":"commerce-web-bff"}]"""), Said(commit)).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Images(Said("not json"), Said(commit)).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Images(Said("[]"), Failed("git: not a repository")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Images(Said("[]"), Said("yesterday")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Images(Said("[]"), Said("2026-10-04T02:19:22+05:00")).State.ShouldBe(DoctorState.Unknown);
    }

    [Fact]
    public void A_clone_whose_status_does_not_answer_or_prints_no_branch_line_is_unknown()
    {
        WorkstationDoctor.Clone("Backend clone", Failed("git: not a repository")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Clone("Backend clone", Said(" M README.md")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.Clone("Backend clone", Said()).State.ShouldBe(DoctorState.Unknown);
    }

    [Fact]
    public void A_cached_rebuild_counts_by_when_it_was_tagged_not_when_its_layers_were_first_created()
    {
        CommandOutput images = Said("""
            [{"ContainerName":"commerce-web-bff-1","Repository":"commerce-web-bff","Created":"2026-10-03T07:18:50Z","LastTagTime":"2026-10-04T09:00:00Z"}]
            """);

        WorkstationDoctor.Images(images, Said("HEAD@{2026-10-04T02:19:22+05:00}")).State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void A_port_docker_holds_for_a_container_outside_this_stack_is_a_problem_that_names_it()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Said($"[{Listener(5000, 36668, "com.docker.backend")}]"), Down);

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("Held by Docker for a container outside this stack, so Up cannot publish them: 5000.");
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("restarting")]
    public void A_port_held_for_this_stacks_container_is_ours_whatever_state_that_container_is_in(string state)
    {
        using JsonDocument model = JsonDocument.Parse(Model);
        ComposeStatus stack = new(true, null, [new ServiceStatus("gateway", state, null, null, [5000])]);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Said($"[{Listener(5000, 36668, "com.docker.backend")}]"), stack);

        check.State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void A_zero_last_tag_time_is_no_time_and_the_creation_time_is_read_instead()
    {
        CommandOutput images = Said("""
            [{"ContainerName":"commerce-web-bff-1","Repository":"commerce-web-bff","Created":"2026-10-04T09:00:00Z","LastTagTime":"0001-01-01T00:00:00Z"}]
            """);

        WorkstationDoctor.Images(images, Said("HEAD@{2026-10-04T02:19:22+05:00}")).State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void An_empty_reflog_is_unknown_and_says_it_printed_nothing()
    {
        DoctorCheck check = WorkstationDoctor.Images(Said("[]"), Said());

        check.State.ShouldBe(DoctorState.Unknown);
        check.Detail.ShouldEndWith("the reflog printed nothing");
    }

    [Fact]
    public void Ports_docker_holds_while_the_stacks_containers_cannot_be_listed_are_unknown_not_fine()
    {
        using JsonDocument model = JsonDocument.Parse(Model);
        ComposeStatus unlisted = new(false, "docker compose ps did not answer within 30 s", []);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Said($"[{Listener(5000, 36668, "com.docker.backend")}]"), unlisted);

        check.State.ShouldBe(DoctorState.Unknown);
        check.Detail.ShouldContain("could not be listed");
    }

    [Fact]
    public void Listeners_that_are_not_the_json_the_line_prints_are_unknown()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        WorkstationDoctor.Ports(model, Said(Model), Said("LocalPort OwningProcess"), Running).State.ShouldBe(DoctorState.Unknown);
    }

    [Fact]
    public void A_model_with_no_gateway_environment_cannot_judge_cors()
    {
        const string bare = """{"services":{"gateway":{"ports":[]}}}""";
        using JsonDocument model = JsonDocument.Parse(bare);

        WorkstationDoctor.Cors(model, Said(bare), "http://localhost:5173").State.ShouldBe(DoctorState.Unknown);
    }

    [Theory]
    [InlineData("""{"Cors__Origins__0":"http://localhost:5173"}""", "unset")]
    [InlineData("""{"Cors__Enabled":"false","Cors__Origins__0":"http://localhost:5173"}""", "false")]
    public void Cors_switched_off_admits_no_origin_whatever_the_list_says(string environment, string value)
    {
        string config = """{"services":{"gateway":{"environment":""" + environment + "}}}";
        using JsonDocument model = JsonDocument.Parse(config);

        DoctorCheck check = WorkstationDoctor.Cors(model, Said(config), "http://localhost:5173");

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain($"{WorkstationDoctor.CorsSwitch} is {value}");
    }

    [Fact]
    public void Docker_that_does_not_answer_is_a_problem_with_its_reason()
    {
        DoctorCheck check = WorkstationDoctor.Docker(Failed("error during connect: the docker daemon is not running"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("daemon is not running");
    }

    [Fact]
    public async Task The_endpoint_answers_every_row_from_the_fake_platform_with_the_recorded_red_rows()
    {
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/stack/doctor", TestContext.Current.CancellationToken);

        Dictionary<string, (string State, string Detail)> rows = view.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => (c.GetProperty("state").GetString()!, c.GetProperty("detail").GetString()!));
        rows.Keys.ShouldBe(["Docker", "Ports", "Gateway CORS", "Node", "Python", "k6", "Backend clone", "Frontend clone", "Images", "Object store"]);
        rows["k6"].ShouldBe(("Ok", "k6.exe v2.2.0 (commit/00a9a1b7f5, go1.26.5, windows/amd64), which the load run runs."));
        rows["Docker"].ShouldBe(("Ok", "Docker 29.7.2 is answering."));
        rows["Ports"].ShouldBe(("Problem", "Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376)."));
        rows["Gateway CORS"].State.ShouldBe("Ok");
        rows["Node"].ShouldBe(("Ok", "Node 22.23.2, as the frontend clone's .nvmrc pins."));
        rows["Python"].ShouldBe(("Ok", "Python 3.12.7, which the dead-letter workbench runs under."));
        rows["Backend clone"].State.ShouldBe("Ok");
        rows["Images"].State.ShouldBe("Problem");
        rows["Object store"].State.ShouldBe("Ok");
    }

    private static ComposeStatus Store(string storeState = "running", string? storeHealth = "healthy", string initState = "exited", int initExit = 0, string? edgeHealth = "healthy") =>
        new(true, null,
        [
            new ServiceStatus("object-store", storeState, storeHealth, 0, [8333]),
            new ServiceStatus("object-store-init", initState, null, initExit, []),
            new ServiceStatus("image-edge", "running", edgeHealth, 0, [9000]),
        ]);

    [Fact]
    public void A_healthy_store_and_edge_with_a_one_shot_that_exited_clean_is_fine()
    {
        DoctorCheck check = WorkstationDoctor.ObjectStore(Store());

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldBe("object-store and image-edge are healthy and object-store-init exited 0.");
    }

    [Fact]
    public void A_one_shot_that_exited_non_zero_is_a_problem_however_green_the_rest_reads()
    {
        DoctorCheck check = WorkstationDoctor.ObjectStore(Store(initExit: 1));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("object-store-init exited 1, so the bucket and its policies may not exist.");
    }

    [Theory]
    [InlineData("exited", null, "object-store is exited.")]
    [InlineData("running", "unhealthy", "object-store is running but unhealthy.")]
    [InlineData("running", null, "object-store is running but has no health.")]
    public void A_store_that_is_not_running_healthy_is_a_problem(string state, string? health, string detail)
    {
        DoctorCheck check = WorkstationDoctor.ObjectStore(Store(storeState: state, storeHealth: health));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe(detail);
    }

    [Fact]
    public void An_edge_or_store_still_starting_and_a_one_shot_still_running_are_unknown_not_a_verdict()
    {
        WorkstationDoctor.ObjectStore(Store(edgeHealth: "starting")).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.ObjectStore(Store(initState: "running")).Detail.ShouldBe("object-store-init is running and has not finished.");
    }

    [Fact]
    public void A_stack_without_the_store_or_that_could_not_be_listed_is_unknown()
    {
        WorkstationDoctor.ObjectStore(Down).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.ObjectStore(new ComposeStatus(true, null, [])).State.ShouldBe(DoctorState.Unknown);
        WorkstationDoctor.ObjectStore(ComposeStatus.Unreachable("docker: not found")).State.ShouldBe(DoctorState.Unknown);
    }
}
