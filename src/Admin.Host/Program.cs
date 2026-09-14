using System.Net;
using System.Text.Json.Serialization;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
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
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobRegistry>();

// Both runners are registered; which one IProcessRunner resolves to is
// decided from options at resolve time, for the same reason RepoPaths is.
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton(sp => FakePlatformScripts.Script(
    new FakeProcessRunner(sp.GetRequiredService<JobRegistry>()),
    sp.GetRequiredService<RepoPaths>().ComposeFile));
builder.Services.AddSingleton<IProcessRunner>(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? sp.GetRequiredService<FakeProcessRunner>()
        : sp.GetRequiredService<ProcessRunner>());

builder.Services.AddSingleton<ComposeService>();
builder.Services.AddHttpClient<PlatformProbe>().ConfigurePrimaryHttpMessageHandler(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? new FakePlatformHandler()
        : new HttpClientHandler());

WebApplication app = builder.Build();

// Resolve once so a wrong directory fails startup with the key to fix.
_ = app.Services.GetRequiredService<RepoPaths>();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapConfig();
app.MapJobs();
app.MapStack();

app.Run();

/// <summary>Visible to WebApplicationFactory in the test project.</summary>
public partial class Program;
