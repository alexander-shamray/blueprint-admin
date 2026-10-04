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

    private static CommandOutput Failed(string error) => CommandOutput.Failed(error);

    private static string Listener(int port, int pid, string? process) =>
        $$"""{"LocalPort":{{port}},"OwningProcess":{{pid}},"Process":{{(process is null ? "null" : $"\"{process}\"")}}}""";

    private static DoctorCheck Ports(params string[] listeners)
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        return WorkstationDoctor.Ports(model, Said(Model), Said($"[{string.Join(',', listeners)}]"));
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
        check.Detail.ShouldBe("1 of 3 published ports are held by Docker, the rest are free.");
    }

    [Fact]
    public void Every_published_port_held_by_docker_is_the_stack_running()
    {
        DoctorCheck check = Ports(Listener(5000, 36668, "com.docker.backend"), Listener(5672, 36668, "com.docker.backend"), Listener(15672, 36668, "com.docker.backend"));

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldBe("All 3 published ports are held by Docker, as they are while the stack runs.");
    }

    [Fact]
    public void One_listener_is_printed_as_an_object_not_an_array_and_still_reads()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Said(Listener(5672, 7376, null)));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("5672 by an unnamed process (pid 7376)");
    }

    [Fact]
    public void No_listeners_at_all_is_every_port_free()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        WorkstationDoctor.Ports(model, Said(Model), Said()).Detail.ShouldBe("All 3 published ports are free.");
    }

    [Fact]
    public void A_listener_read_that_does_not_answer_is_unknown_not_fine()
    {
        using JsonDocument model = JsonDocument.Parse(Model);

        DoctorCheck check = WorkstationDoctor.Ports(model, Said(Model), Failed("powershell: not found"));

        check.State.ShouldBe(DoctorState.Unknown);
        check.Detail.ShouldContain("powershell: not found");
    }

    [Fact]
    public void Without_the_compose_model_neither_ports_nor_cors_can_be_judged()
    {
        WorkstationDoctor.Ports(null, Failed("no such file"), Said()).State.ShouldBe(DoctorState.Unknown);
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

        DoctorCheck check = WorkstationDoctor.Images(images, Said("2026-10-04T02:19:22+05:00"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldBe("1 of 2 built images predate the backend's last commit (2026-10-04 02:19 +05:00), and Up does not rebuild them: commerce-web-bff.");
    }

    [Fact]
    public void No_built_image_running_is_nothing_to_compare_rather_than_fine()
    {
        WorkstationDoctor.Images(Said("[]"), Said("2026-10-04T02:19:22+05:00")).State.ShouldBe(DoctorState.Unknown);
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
        rows.Keys.ShouldBe(["Docker", "Ports", "Gateway CORS", "Node", "Backend clone", "Frontend clone", "Images"]);
        rows["Docker"].ShouldBe(("Ok", "Docker 29.7.2 is answering."));
        rows["Ports"].ShouldBe(("Problem", "Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376)."));
        rows["Gateway CORS"].State.ShouldBe("Ok");
        rows["Node"].ShouldBe(("Ok", "Node 22.23.2, as the frontend clone's .nvmrc pins."));
        rows["Backend clone"].State.ShouldBe("Ok");
        rows["Images"].State.ShouldBe("Problem");
    }
}
