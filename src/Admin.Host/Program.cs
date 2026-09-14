using System.Net;
using System.Text.Json.Serialization;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Security;
using Admin.Host.Stack;
using Microsoft.Extensions.FileProviders;
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

// Before static files and endpoints: a cross-site page must not be able to
// drive /api even though it cannot read the answer (spec §8).
app.UseLoopbackOriginGuard();

// The SPA build lands under this repository-root-relative directory
// (Admin:WebRoot, angular.json's outputPath) regardless of the process's
// working directory or environment: Playwright starts the host from
// src/Admin.Web without ASPNETCORE_ENVIRONMENT=Development, where the
// default content-root "wwwroot" resolution would be wrong. When there is no
// build yet the directory does not exist, so static files and the fallback
// below are skipped entirely and the API is unaffected — PhysicalFileProvider
// throws on a missing root.
string webRootPath = Path.GetFullPath(
    app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.WebRoot, baseDir);
IFileProvider? spaFiles = Directory.Exists(webRootPath) ? new PhysicalFileProvider(webRootPath) : null;

if (spaFiles is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFiles });
}

app.MapConfig();
app.MapJobs();
app.MapStack();

// MapFallbackToFile's route has no literal segments, so without this it
// would also catch an unmatched /api/nope (it has no dot, so it passes the
// :nonfile constraint too) and serve index.html for it. This catch-all has a
// literal "api" segment, which endpoint routing always prefers over the
// fallback's, so a real /api/* route above still wins and only a truly
// unknown one lands here as a genuine 404.
app.Map("/api/{**catchAll}", () => Results.NotFound());

if (spaFiles is not null)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFiles }).ShortCircuit();
}

app.Run();

/// <summary>Visible to WebApplicationFactory in the test project.</summary>
public partial class Program;
