using Admin.Host.Compose;
using Shouldly;

namespace Admin.Host.Tests.Compose;

public sealed class ComposePsParserTests
{
    private const string Gateway = """{"Name":"commerce-gateway-1","Service":"gateway","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":5000,"Protocol":"tcp"}]}""";
    private const string Migrator = """{"Name":"commerce-catalog-migrator-1","Service":"catalog-migrator","State":"exited","Health":"","ExitCode":0,"Publishers":[]}""";

    [Fact]
    public void Parses_one_object_per_line()
    {
        IReadOnlyList<ServiceStatus> services = ComposePsParser.Parse([Gateway, "", Migrator]);

        services.Count.ShouldBe(2);
        services[0].Service.ShouldBe("gateway");
        services[0].State.ShouldBe("running");
        services[0].Health.ShouldBe("healthy");
        services[0].ExitCode.ShouldBe(0);
        services[0].PublishedPorts.ShouldBe([5000]);
        services[1].Service.ShouldBe("catalog-migrator");
        services[1].State.ShouldBe("exited");
        services[1].Health.ShouldBeNull();
        services[1].ExitCode.ShouldBe(0);
        services[1].PublishedPorts.ShouldBeEmpty();
    }

    [Fact]
    public void Parses_a_json_array_from_older_compose()
    {
        IReadOnlyList<ServiceStatus> services = ComposePsParser.Parse([$"[{Gateway},{Migrator}]"]);

        services.Select(s => s.Service).ShouldBe(["gateway", "catalog-migrator"]);
    }

    [Fact]
    public void No_output_is_no_services()
    {
        ComposePsParser.Parse([]).ShouldBeEmpty();
    }

    [Fact]
    public void The_compose_files_a_container_was_created_from_are_read_from_its_labels_by_name()
    {
        const string labels = """com.docker.compose.depends_on=keycloak:service_healthy:false,com.docker.compose.project.config_files=C:\\dev\\blueprint-backend\\deploy\\compose\\docker-compose.yml,C:\\dev\\blueprint-backend\\deploy\\compose\\docker-compose.stress.yml,com.docker.compose.project.working_dir=C:\\dev\\blueprint-backend\\deploy\\compose,com.docker.compose.service=gateway""";
        string line = Gateway.Replace("\"Publishers\"", $"\"Labels\":\"{labels}\",\"Publishers\"", StringComparison.Ordinal);

        ComposePsParser.Parse([line])[0].ConfigFiles.ShouldBe(["docker-compose.yml", "docker-compose.stress.yml"]);
        ComposePsParser.Parse([Gateway])[0].ConfigFiles.ShouldBeNull();
        ComposePsParser.ConfigFiles("com.docker.compose.project.config_files=/srv/deploy/compose/docker-compose.yml").ShouldBe(["docker-compose.yml"]);
    }
}
