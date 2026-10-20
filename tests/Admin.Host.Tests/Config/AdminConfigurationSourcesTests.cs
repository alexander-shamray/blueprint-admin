using Admin.Host.Config;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class AdminConfigurationSourcesTests : IDisposable
{
    // A key nothing binds, so a process-wide variable here cannot reach another test's host.
    private const string Variable = "BLUEPRINT_Admin__ZzSourceOrder";

    public AdminConfigurationSourcesTests()
    {
        Environment.SetEnvironmentVariable(Variable, "from-environment");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
    }

    [Fact]
    public void A_value_on_the_command_line_beats_the_same_key_in_the_environment()
    {
        string[] args = ["--Admin:ZzSourceOrder=from-command-line"];
        ConfigurationBuilder builder = new();
        builder.AddCommandLine(args);

        IConfigurationRoot root = builder.AddAdminSources(args).Build();

        root["Admin:ZzSourceOrder"].ShouldBe("from-command-line");
    }

    [Fact]
    public void The_environment_still_supplies_a_key_the_command_line_does_not_name()
    {
        IConfigurationRoot root = new ConfigurationBuilder().AddAdminSources([]).Build();

        root["Admin:ZzSourceOrder"].ShouldBe("from-environment");
    }
}
