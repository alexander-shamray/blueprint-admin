using System.Net;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("BLUEPRINT_");
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));

// Relative BackendDir/FrontendDir must not resolve against the process's
// working directory: `dotnet run --project src/Admin.Host` from anywhere
// still finds the repo root, since build output always lands under
// artifacts/ (Directory.Build.props) beneath BlueprintAdmin.slnx.
string baseDir = RepoRoot.Find(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;

// Options are read lazily, through IOptions, so that a test factory's
// configuration overrides are honoured wherever they are applied.
builder.Services.AddSingleton(sp => RepoPaths.From(sp.GetRequiredService<IOptions<AdminOptions>>().Value, baseDir));

// Loopback by construction: there is no setting that binds anything else,
// because the host holds realm passwords and can wipe volumes (spec §8).
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    kestrel.Listen(IPAddress.Loopback, context.Configuration.GetValue($"{AdminOptions.Section}:Port", 5300)));

builder.Services.AddProblemDetails();

WebApplication app = builder.Build();

// Resolve once so a wrong directory fails startup with the key to fix.
_ = app.Services.GetRequiredService<RepoPaths>();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapConfig();

app.Run();

/// <summary>Visible to WebApplicationFactory in the test project.</summary>
public partial class Program;
