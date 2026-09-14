using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Admin.Host.Tests.TestSupport;

/// <summary>
/// The host in memory with the platform faked, so an endpoint test needs no
/// clones and no Docker. Tests that need a scripted process runner or HTTP
/// handler replace the registrations in <see cref="ConfigureWebHost"/> through
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
public class AdminHostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:FakePlatform"] = "true",
        }));
    }
}
