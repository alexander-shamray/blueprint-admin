# Blueprint admin, phases 0 and 1: bootstrap, Stack and Logs — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A runnable blueprint-admin repository whose console can start, stop, list and follow the backend Compose stack, and whose FakePlatform mode makes every screen work with no Docker present.

**Architecture:** A .NET 10 minimal API host (`src/Admin.Host`) runs `docker compose` as child processes through a `ProcessRunner`, keeps each run as a `Job` with a ring buffer of output lines, and streams lines to the browser over Server-Sent Events. An Angular 22 SPA (`src/Admin.Web`) is the console; in development it runs on 5301 and proxies `/api` to the host on 5300, and a build lands in the host's `wwwroot` so one URL serves both. `FakePlatform=true` swaps the process runner and the outbound HTTP handler for recorded fakes.

**Tech Stack:** .NET SDK 10.0.302, C# 14, ASP.NET Core minimal APIs, xunit.v3 3.1.0, Shouldly 4.3.0, Microsoft.AspNetCore.Mvc.Testing 10.0.0; Node 22.23.2, Angular 22.1.x, Vitest via `@angular/build:unit-test`, angular-eslint, Prettier, Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` (§4 repository shape, §5.1–5.3 and §5.10 host, §6 Stack and Logs screens, §10 testing, §11 CI, §12 phase 0 and 1). Phases 2 to 6 get their own plan files once this one has landed.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`; `net10.0`, `LangVersion 14.0`, nullable and implicit usings on.
- `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest-Recommended`. IDE0055 (formatting), IDE0065 (usings outside namespace) and IDE0161 (file-scoped namespaces) fail the build. **No column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or editing `.cs` files and before every `dotnet build`, run `dotnet format whitespace BlueprintAdmin.slnx` from the repo root.
- Test names are sentences with underscores (`Follow_replays_buffered_lines_then_streams_live`); CA1707 is off for projects whose name ends in `Tests`.
- The host listens on `127.0.0.1:5300` only. The SPA dev server is `127.0.0.1:5301` with `/api` proxied to 5300.
- Compose project name is `commerce`; the file is `deploy/compose/docker-compose.yml` under the backend clone; service names are `sql`, `redis-cache`, `redis-coordination`, `rabbitmq`, `keycloak`, `otel-collector`, `grafana`, `catalog-migrator`, `catalog-api`, `gateway`, `ordering-migrator`, `ordering-api`, `web-bff`.
- All `dotnet` commands run from the repository root. All `npm` commands run from `src/Admin.Web`.
- Commit messages use `feat:`, `test:`, `chore:`, `docs:` prefixes and end with the attribution trailer the session provides.
- Nothing in the sibling repositories is edited.

---

## File structure

```
.gitattributes, .editorconfig, .gitignore, .nvmrc          Task 1
global.json, Directory.Build.props, Directory.Packages.props Task 1
BlueprintAdmin.slnx                                          Task 2
src/Admin.Host/Admin.Host.csproj                             Task 2
src/Admin.Host/Program.cs                                    Task 2, extended in 5, 7, 8, 12
src/Admin.Host/appsettings.json                              Task 2
src/Admin.Host/Config/AdminOptions.cs                        Task 2   the bound options
src/Admin.Host/Config/RepoPaths.cs                           Task 2   resolved, validated paths
src/Admin.Host/Config/ConfigEndpoints.cs                     Task 2   GET /api/config
src/Admin.Host/Jobs/OutputLine.cs                            Task 3   one captured line
src/Admin.Host/Jobs/ProcessSpec.cs                           Task 3   what to run, where
src/Admin.Host/Jobs/Job.cs                                   Task 3   ring buffer + live subscribers
src/Admin.Host/Jobs/JobRegistry.cs                           Task 3   all jobs, trimmed
src/Admin.Host/Jobs/JobSummary.cs                            Task 3   the wire shape of a job
src/Admin.Host/Jobs/IProcessRunner.cs                        Task 4
src/Admin.Host/Jobs/ProcessRunner.cs                         Task 4   real child processes
src/Admin.Host/Fakes/FakeProcessRunner.cs                    Task 4   scripted runner
src/Admin.Host/Jobs/JobEndpoints.cs                          Task 5   GET /api/jobs, /{id}, /{id}/stream
src/Admin.Host/Jobs/JobStream.cs                             Task 5   SSE events for one job
src/Admin.Host/Compose/ComposeService.cs                     Task 6   up/down/ps/logs/exec
src/Admin.Host/Compose/ComposePsParser.cs                    Task 6   NDJSON → ServiceStatus
src/Admin.Host/Compose/ServiceStatus.cs                      Task 6
src/Admin.Host/Compose/ComposeStatus.cs                      Task 6
src/Admin.Host/Stack/PlatformProbe.cs                        Task 7   reachability of the HTTP surfaces
src/Admin.Host/Stack/StackEndpoints.cs                       Task 7   GET /api/stack, POST up/down, POST /api/logs/follow
src/Admin.Host/Fakes/FakePlatformScripts.cs                  Task 8   the recorded docker outputs
src/Admin.Host/Fakes/FakePlatformHandler.cs                  Task 8   the recorded HTTP surfaces
src/Admin.Host/Fakes/fixtures/compose-ps.jsonl               Task 8
tests/Admin.Host.Tests/Admin.Host.Tests.csproj               Task 2
tests/Admin.Host.Tests/TestSupport/AdminHostFactory.cs       Task 2   WebApplicationFactory with fakes
tests/Admin.Host.Tests/Config/RepoPathsTests.cs              Task 2
tests/Admin.Host.Tests/Config/ConfigEndpointTests.cs         Task 2
tests/Admin.Host.Tests/Jobs/JobTests.cs                      Task 3
tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs              Task 3
tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs            Task 4
tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs       Task 4
tests/Admin.Host.Tests/Jobs/JobEndpointTests.cs              Task 5
tests/Admin.Host.Tests/Compose/ComposePsParserTests.cs       Task 6
tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs        Task 6
tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs           Task 7
tests/Admin.Host.Tests/Stack/StackEndpointTests.cs           Task 7
tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs            Task 8
src/Admin.Web/                                               Task 9   Angular workspace
src/Admin.Web/src/app/core/host/host-types.ts                Task 9   wire types
src/Admin.Web/src/app/core/host/host-client.ts               Task 9   typed calls to /api
src/Admin.Web/src/app/core/host/sse-client.ts                Task 9   EventSource wrapper
src/Admin.Web/src/app/app.ts, app.routes.ts                  Task 9   shell and routes
src/Admin.Web/src/app/shared/output-pane/output-pane.ts      Task 10  live job output
src/Admin.Web/src/app/features/stack/stack-page.ts           Task 10
src/Admin.Web/src/app/features/logs/logs-page.ts             Task 11
src/Admin.Web/e2e/stack.spec.ts, logs.spec.ts                Task 12  Playwright smoke
src/Admin.Web/playwright.config.ts                           Task 12
.github/workflows/ci.yml                                     Task 13
README.md, CLAUDE.md                                         Task 14
```

---

### Task 1: Repository bootstrap

**Files:**
- Create: `.gitattributes`, `.editorconfig`, `.gitignore`, `.nvmrc`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`

**Interfaces:**
- Produces: the build settings every later `.csproj` inherits; `Directory.Packages.props` owns every package version, so a `.csproj` never carries a `Version` attribute.

- [ ] **Step 1: Copy the editorconfig and SDK pin from the backend**

Run from the repo root:

```bash
cp ../blueprint-backend/.editorconfig .editorconfig
cp ../blueprint-backend/global.json global.json
cp ../blueprint-frontend/.nvmrc .nvmrc
```

Expected: `cat global.json` shows `"version": "10.0.302"`, `cat .nvmrc` shows `22.23.2`.

- [ ] **Step 2: Write `.gitattributes`**

```gitattributes
# .editorconfig asks for CRLF and IDE0055 is a build error, so a .cs that
# arrives LF fails the build on every line. Normalise to LF in the store and
# check out CRLF for C# everywhere, as the backend does.
*.cs text eol=crlf

# Everything else is LF: shell, Python and YAML are read on Linux runners,
# and the Angular toolchain is indifferent.
* text=auto eol=lf
```

- [ ] **Step 3: Write `.gitignore`**

```gitignore
# Build output for every project, per Directory.Build.props's Output group.
/artifacts/
[Bb]in/
[Oo]bj/

# The SPA build lands in the host's wwwroot so one URL serves both; it is
# produced by `ng build`, never committed.
/src/Admin.Host/wwwroot/

# Angular
/src/Admin.Web/node_modules/
/src/Admin.Web/dist/
/src/Admin.Web/.angular/
/src/Admin.Web/test-results/
/src/Admin.Web/playwright-report/

# Per-developer overrides
.claude/settings.local.json
.remember/
```

- [ ] **Step 4: Write `Directory.Build.props`**

```xml
<Project>

  <!-- Target: the same platform-wide decision as the backend's, .NET 10 LTS and C# 14. -->
  <PropertyGroup Label="Target">
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <!-- Output: one artifacts/ directory at the root, nothing beside a .csproj. -->
  <PropertyGroup Label="Output">
    <UseArtifactsOutput>true</UseArtifactsOutput>
    <ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
  </PropertyGroup>

  <!-- Analysis: the backend's ADR-019 policy. IDE0055, IDE0065 and IDE0161 are
       build errors through .editorconfig; everything else stays a suggestion. -->
  <PropertyGroup Label="Analysis">
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-Recommended</AnalysisLevel>
  </PropertyGroup>

  <!-- Test names are sentences with underscores; CA1707 would ban them. -->
  <PropertyGroup Label="Test analysis" Condition="$(MSBuildProjectName.EndsWith('Tests'))">
    <NoWarn>$(NoWarn);CA1707</NoWarn>
  </PropertyGroup>

  <PropertyGroup Label="Build">
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>

</Project>
```

- [ ] **Step 5: Write `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup Label="Test">
    <PackageVersion Include="xunit.v3" Version="3.1.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Verify the SDK resolves**

Run: `dotnet --version`
Expected: `10.0.302`

- [ ] **Step 7: Commit**

```bash
git add .gitattributes .editorconfig .gitignore .nvmrc global.json Directory.Build.props Directory.Packages.props
git commit -m "chore: bootstrap repository settings from the sibling repos"
```

---

### Task 2: Host skeleton, options and `GET /api/config`

**Files:**
- Create: `BlueprintAdmin.slnx`, `src/Admin.Host/Admin.Host.csproj`, `src/Admin.Host/Program.cs`, `src/Admin.Host/appsettings.json`, `src/Admin.Host/Config/AdminOptions.cs`, `src/Admin.Host/Config/RepoPaths.cs`, `src/Admin.Host/Config/ConfigEndpoints.cs`
- Create: `tests/Admin.Host.Tests/Admin.Host.Tests.csproj`, `tests/Admin.Host.Tests/TestSupport/AdminHostFactory.cs`, `tests/Admin.Host.Tests/Config/RepoPathsTests.cs`, `tests/Admin.Host.Tests/Config/ConfigEndpointTests.cs`

**Interfaces:**
- Produces: `AdminOptions` (section `Admin`), `RepoPaths { string BackendDir; string FrontendDir; string ComposeFile; }` registered as a singleton, `RepoPaths.From(AdminOptions, string baseDir)`, `public partial class Program`, and `AdminHostFactory` for endpoint tests. Every later endpoint test derives from `AdminHostFactory`.

- [ ] **Step 1: Create the solution and both projects**

`BlueprintAdmin.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Admin.Host/Admin.Host.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Admin.Host.Tests/Admin.Host.Tests.csproj" />
  </Folder>
</Solution>
```

`src/Admin.Host/Admin.Host.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- Settings come from Directory.Build.props. No packages in phase 1: the
       shared framework carries minimal APIs, options, HttpClientFactory and
       Server-Sent Events. -->

  <ItemGroup>
    <InternalsVisibleTo Include="Admin.Host.Tests" />
  </ItemGroup>

</Project>
```

`tests/Admin.Host.Tests/Admin.Host.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- xUnit v3 test projects are self-executing, so the assembly is an Exe. -->
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Admin.Host\Admin.Host.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the failing tests for `RepoPaths`**

`tests/Admin.Host.Tests/Config/RepoPathsTests.cs`:

```csharp
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class RepoPathsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("admin-paths-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string MakeClones()
    {
        Directory.CreateDirectory(Path.Combine(root, "blueprint-backend", "deploy", "compose"));
        File.WriteAllText(Path.Combine(root, "blueprint-backend", "deploy", "compose", "docker-compose.yml"), "name: commerce\n");
        Directory.CreateDirectory(Path.Combine(root, "blueprint-frontend"));
        File.WriteAllText(Path.Combine(root, "blueprint-frontend", "package.json"), "{}");

        return Path.Combine(root, "blueprint-admin");
    }

    [Fact]
    public void Resolves_the_defaults_against_the_base_directory()
    {
        string baseDir = MakeClones();

        RepoPaths paths = RepoPaths.From(new AdminOptions(), baseDir);

        paths.BackendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "blueprint-backend")));
        paths.FrontendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "blueprint-frontend")));
        paths.ComposeFile.ShouldBe(Path.Combine(paths.BackendDir, "deploy", "compose", "docker-compose.yml"));
    }

    [Fact]
    public void Names_the_key_when_the_backend_clone_is_missing()
    {
        string baseDir = MakeClones();
        Directory.Delete(Path.Combine(root, "blueprint-backend"), recursive: true);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:BackendDir");
    }

    [Fact]
    public void Names_the_key_when_the_compose_file_is_missing()
    {
        string baseDir = MakeClones();
        File.Delete(Path.Combine(root, "blueprint-backend", "deploy", "compose", "docker-compose.yml"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:ComposeFile");
    }

    [Fact]
    public void Names_the_key_when_the_frontend_has_no_package_json()
    {
        string baseDir = MakeClones();
        File.Delete(Path.Combine(root, "blueprint-frontend", "package.json"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:FrontendDir");
    }

    [Fact]
    public void Skips_validation_when_the_platform_is_fake()
    {
        AdminOptions options = new() { FakePlatform = true, BackendDir = "nowhere", FrontendDir = "nowhere" };

        RepoPaths paths = RepoPaths.From(options, root);

        paths.BackendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "nowhere")));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: build FAILS with `The type or namespace name 'AdminOptions' could not be found`.

- [ ] **Step 4: Write `AdminOptions` and `RepoPaths`**

`src/Admin.Host/Config/AdminOptions.cs`:

```csharp
namespace Admin.Host.Config;

/// <summary>
/// Everything the console needs to know about the workstation, bound from the
/// <c>Admin</c> section of appsettings, from <c>BLUEPRINT_Admin__Key</c>
/// environment variables and from <c>--Admin:Key=value</c> on the command line.
/// Relative directories resolve against the working directory the host was
/// started from, so the README says to start it from the repository root.
/// </summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string BackendDir { get; set; } = "../blueprint-backend";

    public string FrontendDir { get; set; } = "../blueprint-frontend";

    public string ComposeFile { get; set; } = "deploy/compose/docker-compose.yml";

    public int Port { get; set; } = 5300;

    public string GatewayUrl { get; set; } = "http://localhost:5000";

    public string CatalogUrl { get; set; } = "http://localhost:5102";

    public string OrderingUrl { get; set; } = "http://localhost:5101";

    public string BffUrl { get; set; } = "http://localhost:5200";

    public string KeycloakUrl { get; set; } = "http://localhost:8080";

    public string GrafanaUrl { get; set; } = "http://localhost:3000";

    public string ClientUrl { get; set; } = "http://localhost:5173";

    public string Realm { get; set; } = "commerce";

    public string ClientId { get; set; } = "web-app";

    /// <summary>
    /// Swap every child process and every outbound HTTP call for recorded
    /// fakes, so the console runs with no Docker and no clones. This is how
    /// the SPA is developed and how CI runs the Playwright smoke.
    /// </summary>
    public bool FakePlatform { get; set; }
}
```

`src/Admin.Host/Config/RepoPaths.cs`:

```csharp
namespace Admin.Host.Config;

/// <summary>
/// The two clones and the Compose file as absolute paths, validated once at
/// startup so that a wrong directory fails the host with the key to fix
/// rather than failing the first command with "file not found".
/// </summary>
public sealed record RepoPaths(string BackendDir, string FrontendDir, string ComposeFile)
{
    public static RepoPaths From(AdminOptions options, string baseDir)
    {
        string backend = Path.GetFullPath(options.BackendDir, baseDir);
        string frontend = Path.GetFullPath(options.FrontendDir, baseDir);
        string compose = Path.GetFullPath(options.ComposeFile, backend);

        if (!options.FakePlatform)
        {
            if (!Directory.Exists(backend))
            {
                throw new InvalidOperationException($"Admin:BackendDir resolves to '{backend}', which does not exist.");
            }

            if (!File.Exists(compose))
            {
                throw new InvalidOperationException($"Admin:ComposeFile resolves to '{compose}', which does not exist.");
            }

            if (!File.Exists(Path.Combine(frontend, "package.json")))
            {
                throw new InvalidOperationException($"Admin:FrontendDir resolves to '{frontend}', which has no package.json.");
            }
        }

        return new RepoPaths(backend, frontend, compose);
    }
}
```

- [ ] **Step 5: Run the `RepoPaths` tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~RepoPathsTests"`
Expected: 5 passed.

- [ ] **Step 6: Write the failing endpoint test and the test factory**

`tests/Admin.Host.Tests/TestSupport/AdminHostFactory.cs`:

```csharp
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
```

`tests/Admin.Host.Tests/Config/ConfigEndpointTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class ConfigEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public ConfigEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Reports_the_resolved_paths_and_urls()
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/api/config", TestContext.Current.CancellationToken);

        body.GetProperty("fakePlatform").GetBoolean().ShouldBeTrue();
        body.GetProperty("backendDir").GetString().ShouldEndWith("blueprint-backend");
        body.GetProperty("urls").GetProperty("gateway").GetString().ShouldBe("http://localhost:5000");
        body.GetProperty("urls").GetProperty("client").GetString().ShouldBe("http://localhost:5173");
    }
}
```

- [ ] **Step 7: Run the endpoint test to verify it fails**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~ConfigEndpointTests"`
Expected: build FAILS, `Program` not found.

- [ ] **Step 8: Write `Program.cs`, `appsettings.json` and the config endpoint**

`src/Admin.Host/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Admin": {
    "BackendDir": "../blueprint-backend",
    "FrontendDir": "../blueprint-frontend",
    "ComposeFile": "deploy/compose/docker-compose.yml",
    "Port": 5300,
    "FakePlatform": false
  }
}
```

`src/Admin.Host/Config/ConfigEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Admin.Host.Config;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (IOptions<AdminOptions> options, RepoPaths paths) =>
        {
            AdminOptions o = options.Value;

            return TypedResults.Ok(new ConfigView(
                paths.BackendDir,
                paths.FrontendDir,
                paths.ComposeFile,
                o.FakePlatform,
                new UrlsView(o.GatewayUrl, o.CatalogUrl, o.OrderingUrl, o.BffUrl, o.KeycloakUrl, o.GrafanaUrl, o.ClientUrl)));
        });

        return app;
    }
}

public sealed record ConfigView(string BackendDir, string FrontendDir, string ComposeFile, bool FakePlatform, UrlsView Urls);

public sealed record UrlsView(string Gateway, string Catalog, string Ordering, string Bff, string Keycloak, string Grafana, string Client);
```

`src/Admin.Host/Program.cs`:

```csharp
using System.Net;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("BLUEPRINT_");
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));

// Options are read lazily, through IOptions, so that a test factory's
// configuration overrides are honoured wherever they are applied.
builder.Services.AddSingleton(sp => RepoPaths.From(sp.GetRequiredService<IOptions<AdminOptions>>().Value, Environment.CurrentDirectory));

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
```

- [ ] **Step 9: Run all tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: 6 passed.

- [ ] **Step 10: Run the host for real and check the loopback bind**

Run (from the repo root; both clones exist beside it): `dotnet run --project src/Admin.Host`
Expected: the log shows `Now listening on: http://127.0.0.1:5300`. In another shell `curl -s http://127.0.0.1:5300/api/config` prints JSON with `"fakePlatform":false`. Stop with Ctrl+C.

- [ ] **Step 11: Commit**

```bash
git add BlueprintAdmin.slnx src/Admin.Host tests/Admin.Host.Tests
git commit -m "feat(host): options, validated repo paths and GET /api/config"
```

---

### Task 3: Jobs — the ring buffer and live subscribers

**Files:**
- Create: `src/Admin.Host/Jobs/OutputLine.cs`, `src/Admin.Host/Jobs/ProcessSpec.cs`, `src/Admin.Host/Jobs/Job.cs`, `src/Admin.Host/Jobs/JobRegistry.cs`, `src/Admin.Host/Jobs/JobSummary.cs`
- Test: `tests/Admin.Host.Tests/Jobs/JobTests.cs`, `tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs`

**Interfaces:**
- Produces:
  - `enum OutputStream { Stdout, Stderr }`, `enum JobState { Running, Exited }`
  - `record OutputLine(long Sequence, DateTimeOffset At, OutputStream Stream, string Text)`
  - `record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)` with `string CommandLine`
  - `class Job`: `string Id`, `ProcessSpec Spec`, `JobState State`, `int? ExitCode`, `DateTimeOffset StartedAt`, `Task<int> Completion`, `void Append(OutputStream, string)`, `void MarkExited(int)`, `IReadOnlyList<OutputLine> Since(long afterSequence)`, `IReadOnlyList<OutputLine> Tail(int count)`, `IAsyncEnumerable<OutputLine> Follow(long afterSequence, CancellationToken)`
  - `class JobRegistry`: `Job Create(ProcessSpec)`, `Job? Find(string id)`, `IReadOnlyList<Job> All()`, `const int KeepExited = 50`
  - `record JobSummary(string Id, string CommandLine, JobState State, int? ExitCode, DateTimeOffset StartedAt)` with `static JobSummary Of(Job)`

- [ ] **Step 1: Write the failing `Job` tests**

`tests/Admin.Host.Tests/Jobs/JobTests.cs`:

```csharp
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "ps"], "/tmp");

    private static Job NewJob(int capacity = 2000) => new("job-1", Spec, new FakeTimeProvider(), capacity);

    [Fact]
    public void Appended_lines_are_numbered_from_zero()
    {
        Job job = NewJob();

        job.Append(OutputStream.Stdout, "one");
        job.Append(OutputStream.Stderr, "two");

        job.Since(-1).Select(l => (l.Sequence, l.Stream, l.Text)).ShouldBe([(0L, OutputStream.Stdout, "one"), (1L, OutputStream.Stderr, "two")]);
    }

    [Fact]
    public void The_ring_keeps_only_the_last_capacity_lines()
    {
        Job job = NewJob(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            job.Append(OutputStream.Stdout, $"line {i}");
        }

        job.Since(-1).Select(l => l.Text).ShouldBe(["line 2", "line 3", "line 4"]);
        job.Tail(2).Select(l => l.Text).ShouldBe(["line 3", "line 4"]);
    }

    [Fact]
    public void Since_returns_only_lines_after_the_given_sequence()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "a");
        job.Append(OutputStream.Stdout, "b");
        job.Append(OutputStream.Stdout, "c");

        job.Since(0).Select(l => l.Text).ShouldBe(["b", "c"]);
        job.Since(2).ShouldBeEmpty();
    }

    [Fact]
    public async Task Follow_replays_buffered_lines_then_streams_live_until_exit()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "before");
        List<string> seen = [];

        Task reader = Task.Run(async () =>
        {
            await foreach (OutputLine line in job.Follow(-1, TestContext.Current.CancellationToken))
            {
                seen.Add(line.Text);
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        job.Append(OutputStream.Stdout, "during");
        job.MarkExited(0);
        await reader.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        seen.ShouldBe(["before", "during"]);
        job.State.ShouldBe(JobState.Exited);
        job.ExitCode.ShouldBe(0);
        (await job.Completion).ShouldBe(0);
    }

    [Fact]
    public async Task Follow_on_an_exited_job_replays_and_completes()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "only");
        job.MarkExited(3);

        List<string> seen = [];
        await foreach (OutputLine line in job.Follow(-1, TestContext.Current.CancellationToken))
        {
            seen.Add(line.Text);
        }

        seen.ShouldBe(["only"]);
    }

    [Fact]
    public void MarkExited_twice_keeps_the_first_exit_code()
    {
        Job job = NewJob();

        job.MarkExited(1);
        job.MarkExited(2);

        job.ExitCode.ShouldBe(1);
    }
}
```

`Microsoft.Extensions.Time.Testing` ships in the `Microsoft.Extensions.TimeProvider.Testing` package. Add to `Directory.Packages.props` under the `Test` group:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```

and to the test csproj's package group:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

- [ ] **Step 2: Write the failing `JobRegistry` tests**

`tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs`:

```csharp
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobRegistryTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "ps"], "/tmp");

    [Fact]
    public void Create_registers_a_running_job_with_a_unique_id()
    {
        JobRegistry registry = new(new FakeTimeProvider());

        Job first = registry.Create(Spec);
        Job second = registry.Create(Spec);

        first.Id.ShouldNotBe(second.Id);
        first.State.ShouldBe(JobState.Running);
        registry.Find(first.Id).ShouldBeSameAs(first);
        registry.All().Count.ShouldBe(2);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_id()
    {
        JobRegistry registry = new(new FakeTimeProvider());

        registry.Find("nope").ShouldBeNull();
    }

    [Fact]
    public void Exited_jobs_beyond_the_keep_limit_are_dropped_oldest_first()
    {
        FakeTimeProvider time = new();
        JobRegistry registry = new(time);
        List<Job> jobs = [];

        for (int i = 0; i < JobRegistry.KeepExited + 2; i++)
        {
            Job job = registry.Create(Spec);
            job.MarkExited(0);
            jobs.Add(job);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Job running = registry.Create(Spec);

        registry.Find(jobs[0].Id).ShouldBeNull();
        registry.Find(jobs[1].Id).ShouldBeNull();
        registry.Find(jobs[2].Id).ShouldNotBeNull();
        registry.Find(running.Id).ShouldNotBeNull();
        registry.All().Count.ShouldBe(JobRegistry.KeepExited + 1);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Jobs"`
Expected: build FAILS with `Job` not found.

- [ ] **Step 4: Write the job types**

`src/Admin.Host/Jobs/OutputLine.cs`:

```csharp
namespace Admin.Host.Jobs;

public enum OutputStream
{
    Stdout,
    Stderr,
}

public enum JobState
{
    Running,
    Exited,
}

/// <summary>One line a child process wrote. Sequence numbers start at zero per job and never repeat.</summary>
public sealed record OutputLine(long Sequence, DateTimeOffset At, OutputStream Stream, string Text);
```

`src/Admin.Host/Jobs/ProcessSpec.cs`:

```csharp
namespace Admin.Host.Jobs;

/// <summary>What to run and where. Arguments are passed as a list, never joined into a shell string.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";
}
```

`src/Admin.Host/Jobs/Job.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Admin.Host.Jobs;

/// <summary>
/// One run of a child process: its output as a bounded ring of numbered lines,
/// and a channel per live follower. One-shot commands and long-running ones
/// are the same type so that every command's output is visible the same way.
/// </summary>
public sealed class Job
{
    private readonly Lock gate = new();
    private readonly OutputLine?[] ring;
    private readonly List<Channel<OutputLine>> followers = [];
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider time;
    private long next;

    public Job(string id, ProcessSpec spec, TimeProvider time, int capacity = 2000)
    {
        Id = id;
        Spec = spec;
        this.time = time;
        ring = new OutputLine?[capacity];
        StartedAt = time.GetUtcNow();
    }

    public string Id { get; }

    public ProcessSpec Spec { get; }

    public DateTimeOffset StartedAt { get; }

    public JobState State { get; private set; } = JobState.Running;

    public int? ExitCode { get; private set; }

    public Task<int> Completion => completion.Task;

    public void Append(OutputStream stream, string text)
    {
        lock (gate)
        {
            OutputLine line = new(next, time.GetUtcNow(), stream, text);
            ring[next % ring.Length] = line;
            next++;

            foreach (Channel<OutputLine> follower in followers)
            {
                follower.Writer.TryWrite(line);
            }
        }
    }

    public void MarkExited(int exitCode)
    {
        lock (gate)
        {
            if (State == JobState.Exited)
            {
                return;
            }

            State = JobState.Exited;
            ExitCode = exitCode;

            foreach (Channel<OutputLine> follower in followers)
            {
                follower.Writer.TryComplete();
            }
        }

        completion.TrySetResult(exitCode);
    }

    /// <summary>Buffered lines with a sequence greater than <paramref name="afterSequence"/>. Pass -1 for everything still in the ring.</summary>
    public IReadOnlyList<OutputLine> Since(long afterSequence)
    {
        lock (gate)
        {
            return SinceUnlocked(afterSequence);
        }
    }

    public IReadOnlyList<OutputLine> Tail(int count)
    {
        lock (gate)
        {
            return SinceUnlocked(next - count - 1);
        }
    }

    /// <summary>Replay what is buffered after <paramref name="afterSequence"/>, then every new line until the job exits.</summary>
    public IAsyncEnumerable<OutputLine> Follow(long afterSequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<OutputLine> replay;
        Channel<OutputLine>? live = null;

        lock (gate)
        {
            replay = SinceUnlocked(afterSequence);

            if (State == JobState.Running)
            {
                live = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions { SingleReader = true });
                followers.Add(live);
            }
        }

        return FollowCore(replay, live, cancellationToken);
    }

    private async IAsyncEnumerable<OutputLine> FollowCore(
        IReadOnlyList<OutputLine> replay,
        Channel<OutputLine>? live,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (OutputLine line in replay)
        {
            yield return line;
        }

        if (live is null)
        {
            yield break;
        }

        try
        {
            await foreach (OutputLine line in live.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
            }
        }
        finally
        {
            lock (gate)
            {
                followers.Remove(live);
            }
        }
    }

    private List<OutputLine> SinceUnlocked(long afterSequence)
    {
        long first = Math.Max(afterSequence + 1, next - ring.Length);
        List<OutputLine> lines = [];

        for (long sequence = Math.Max(first, 0); sequence < next; sequence++)
        {
            lines.Add(ring[sequence % ring.Length]!);
        }

        return lines;
    }
}
```

`src/Admin.Host/Jobs/JobRegistry.cs`:

```csharp
using System.Collections.Concurrent;

namespace Admin.Host.Jobs;

/// <summary>Every job the host has started, in memory for the host's lifetime, exited ones trimmed past the last fifty.</summary>
public sealed class JobRegistry(TimeProvider time)
{
    public const int KeepExited = 50;

    private readonly ConcurrentDictionary<string, Job> jobs = new();

    public Job Create(ProcessSpec spec)
    {
        Job job = new(Guid.CreateVersion7().ToString("N"), spec, time);
        jobs[job.Id] = job;
        Trim();

        return job;
    }

    public Job? Find(string id) => jobs.GetValueOrDefault(id);

    public IReadOnlyList<Job> All() => jobs.Values.OrderBy(j => j.StartedAt).ToList();

    private void Trim()
    {
        List<Job> exited = jobs.Values.Where(j => j.State == JobState.Exited).OrderBy(j => j.StartedAt).ToList();

        foreach (Job stale in exited.Take(Math.Max(0, exited.Count - KeepExited)))
        {
            jobs.TryRemove(stale.Id, out _);
        }
    }
}
```

`src/Admin.Host/Jobs/JobSummary.cs`:

```csharp
namespace Admin.Host.Jobs;

/// <summary>The wire shape of a job: everything but its lines.</summary>
public sealed record JobSummary(string Id, string CommandLine, JobState State, int? ExitCode, DateTimeOffset StartedAt)
{
    public static JobSummary Of(Job job) => new(job.Id, job.Spec.CommandLine, job.State, job.ExitCode, job.StartedAt);
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Jobs"`
Expected: 9 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Host/Jobs tests/Admin.Host.Tests/Jobs Directory.Packages.props tests/Admin.Host.Tests/Admin.Host.Tests.csproj
git commit -m "feat(host): jobs with a ring buffer and live followers"
```

---

### Task 4: ProcessRunner and FakeProcessRunner

**Files:**
- Create: `src/Admin.Host/Jobs/IProcessRunner.cs`, `src/Admin.Host/Jobs/ProcessRunner.cs`, `src/Admin.Host/Fakes/FakeProcessRunner.cs`
- Test: `tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs`, `tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs`

**Interfaces:**
- Consumes: `Job`, `JobRegistry`, `ProcessSpec` from Task 3.
- Produces:
  - `interface IProcessRunner { Job Start(ProcessSpec spec); Task StopAsync(Job job, CancellationToken ct); }`
  - `class ProcessRunner(JobRegistry registry, ILogger<ProcessRunner> logger) : IProcessRunner`
  - `class FakeProcessRunner(JobRegistry registry) : IProcessRunner` with `FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)`, `FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines)`, `IReadOnlyList<ProcessSpec> Started`.
  - Scripts match when `spec.FileName == fileName` and `string.Join(' ', spec.Arguments)` starts with `argumentPrefix`; the first match wins; an unmatched spec yields a job that writes `fake: no script for <command line>` to stderr and exits 127.

- [ ] **Step 1: Write the failing `ProcessRunner` tests**

These use `node`, which every developer and CI runner has (the SPA needs it), because it is the one long-running, line-emitting command that behaves the same on Windows and Linux.

`tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs`:

```csharp
using Admin.Host.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class ProcessRunnerTests
{
    private readonly JobRegistry registry = new(TimeProvider.System);

    private ProcessRunner Runner => new(registry, NullLogger<ProcessRunner>.Instance);

    [Fact]
    public async Task Captures_stdout_and_stderr_and_the_exit_code()
    {
        ProcessSpec spec = new("node", ["-e", "console.log('out'); console.error('err'); process.exit(3)"], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        exit.ShouldBe(3);
        job.State.ShouldBe(JobState.Exited);
        job.Since(-1).Select(l => (l.Stream, l.Text)).ShouldBe([(OutputStream.Stdout, "out"), (OutputStream.Stderr, "err")], ignoreOrder: true);
        registry.Find(job.Id).ShouldBeSameAs(job);
    }

    [Fact]
    public async Task Stop_kills_a_long_running_process()
    {
        ProcessSpec spec = new("node", ["-e", "setInterval(() => console.log('tick'), 50)"], Environment.CurrentDirectory);
        ProcessRunner runner = Runner;

        Job job = runner.Start(spec);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Running);
        job.Since(-1).ShouldNotBeEmpty();

        await runner.StopAsync(job, TestContext.Current.CancellationToken);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
        job.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task A_missing_executable_is_an_exited_job_not_an_exception()
    {
        ProcessSpec spec = new("definitely-not-on-path-4f2c", [], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(-1);
        job.Since(-1).Single().Stream.ShouldBe(OutputStream.Stderr);
        job.Since(-1).Single().Text.ShouldContain("definitely-not-on-path-4f2c");
    }
}
```

- [ ] **Step 2: Write the failing `FakeProcessRunner` tests**

`tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs`:

```csharp
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakeProcessRunnerTests
{
    private readonly JobRegistry registry = new(new FakeTimeProvider());

    [Fact]
    public async Task A_scripted_command_emits_its_lines_and_exits()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .On("docker", "compose -f x ps", 0, "{\"Service\":\"gateway\"}");

        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "x", "ps", "-a", "--format", "json"], "/b"));
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        job.Since(-1).Single().Text.ShouldBe("{\"Service\":\"gateway\"}");
        runner.Started.Single().CommandLine.ShouldBe("docker compose -f x ps -a --format json");
    }

    [Fact]
    public async Task A_long_running_script_stays_running_until_stopped()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .OnLongRunning("docker", "compose -f x logs", "gateway | started");

        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "x", "logs", "-f"], "/b"));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Running);
        job.Since(-1).Single().Text.ShouldBe("gateway | started");

        await runner.StopAsync(job, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task An_unscripted_command_exits_127_with_a_message()
    {
        FakeProcessRunner runner = new(registry);

        Job job = runner.Start(new ProcessSpec("npm", ["start"], "/f"));
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(127);
        job.Since(-1).Single().Text.ShouldBe("fake: no script for npm start");
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~ProcessRunner"`
Expected: build FAILS, `IProcessRunner` and `ProcessRunner` not found.

- [ ] **Step 4: Write the interface and the real runner**

`src/Admin.Host/Jobs/IProcessRunner.cs`:

```csharp
namespace Admin.Host.Jobs;

public interface IProcessRunner
{
    /// <summary>Start the process and return its job immediately. A process that cannot start is a job that has already exited with -1.</summary>
    Job Start(ProcessSpec spec);

    /// <summary>Kill the process and its whole tree, then wait for the job to exit. A job that has exited is a no-op.</summary>
    Task StopAsync(Job job, CancellationToken cancellationToken);
}
```

`src/Admin.Host/Jobs/ProcessRunner.cs`:

```csharp
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Admin.Host.Jobs;

/// <summary>
/// Real child processes. Output is read line by line on the process's own
/// threads; the ring buffer in <see cref="Job"/> is what makes that safe to
/// read from a request. This is the one type that knows a process has a tree,
/// which is what makes <c>ng serve</c> stoppable on Windows.
/// </summary>
public sealed class ProcessRunner(JobRegistry registry, ILogger<ProcessRunner> logger) : IProcessRunner
{
    private readonly ConcurrentDictionary<string, Process> processes = new();

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);

        ProcessStartInfo info = new(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = new() { StartInfo = info, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                job.Append(OutputStream.Stdout, e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                job.Append(OutputStream.Stderr, e.Data);
            }
        };

        process.Exited += (_, _) =>
        {
            // WaitForExit without a timeout also waits for the async readers to drain.
            process.WaitForExit();
            job.MarkExited(process.ExitCode);
            processes.TryRemove(job.Id, out _);
            process.Dispose();
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not start {CommandLine}", spec.CommandLine);
            job.Append(OutputStream.Stderr, $"Could not start '{spec.FileName}': {ex.Message}");
            job.MarkExited(-1);
            process.Dispose();

            return job;
        }

        processes[job.Id] = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return job;
    }

    public async Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        if (!processes.TryGetValue(job.Id, out Process? process))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the lookup and the kill.
        }

        await job.Completion.WaitAsync(cancellationToken);
    }
}
```

- [ ] **Step 5: Write the fake runner**

`src/Admin.Host/Fakes/FakeProcessRunner.cs`:

```csharp
using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// A process runner that replays scripts instead of starting processes. Lives
/// in the host, not the test project, because FakePlatform mode ships it.
/// </summary>
public sealed class FakeProcessRunner(JobRegistry registry) : IProcessRunner
{
    private readonly List<FakeScript> scripts = [];
    private readonly List<ProcessSpec> started = [];

    public IReadOnlyList<ProcessSpec> Started => started;

    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)
    {
        scripts.Add(new FakeScript(fileName, argumentPrefix, exitCode, lines));

        return this;
    }

    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines)
    {
        scripts.Add(new FakeScript(fileName, argumentPrefix, null, lines));

        return this;
    }

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);
        started.Add(spec);

        string arguments = string.Join(' ', spec.Arguments);
        FakeScript? script = scripts.FirstOrDefault(s => s.FileName == spec.FileName && arguments.StartsWith(s.ArgumentPrefix, StringComparison.Ordinal));

        if (script is null)
        {
            job.Append(OutputStream.Stderr, $"fake: no script for {spec.CommandLine}");
            job.MarkExited(127);

            return job;
        }

        foreach (string line in script.Lines)
        {
            job.Append(OutputStream.Stdout, line);
        }

        if (script.ExitCode is int exitCode)
        {
            job.MarkExited(exitCode);
        }

        return job;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        job.MarkExited(-1);

        return Task.CompletedTask;
    }

    private sealed record FakeScript(string FileName, string ArgumentPrefix, int? ExitCode, IReadOnlyList<string> Lines);
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~ProcessRunner"`
Expected: 6 passed. If `Captures_stdout_and_stderr_and_the_exit_code` fails with a `Win32Exception` on Windows, `node` is not on the PATH of the test host; fix the PATH, do not change the test.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Host/Jobs src/Admin.Host/Fakes tests/Admin.Host.Tests
git commit -m "feat(host): real and fake process runners"
```

---

### Task 5: Job endpoints and the SSE stream

**Files:**
- Create: `src/Admin.Host/Jobs/JobStream.cs`, `src/Admin.Host/Jobs/JobEndpoints.cs`
- Modify: `src/Admin.Host/Program.cs` (register `TimeProvider`, `JobRegistry`, `IProcessRunner`; call `MapJobs()`)
- Test: `tests/Admin.Host.Tests/Jobs/JobEndpointTests.cs`

**Interfaces:**
- Consumes: `Job`, `JobRegistry`, `JobSummary`, `FakeProcessRunner`.
- Produces:
  - `GET /api/jobs` → `JobSummary[]`; `GET /api/jobs/{id}` → `JobView(JobSummary Summary, OutputLine[] Lines)` (last 200 lines); 404 problem for an unknown id.
  - `GET /api/jobs/{id}/stream?after=N` → `text/event-stream`. Events: `line` with data `OutputLine` as JSON and `id:` = sequence; then one `exited` with data `{"exitCode":N}`. `after` comes from the query string or the `Last-Event-ID` header (query wins); default -1.
  - `Program` registers `IProcessRunner` as `FakeProcessRunner` when `Admin:FakePlatform` is true, else `ProcessRunner`. Tests reach the fake through `factory.Services.GetRequiredService<FakeProcessRunner>()`.

- [ ] **Step 1: Write the failing endpoint tests**

`tests/Admin.Host.Tests/Jobs/JobEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public JobEndpointTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private FakeProcessRunner Runner => factory.Services.GetRequiredService<FakeProcessRunner>();

    [Fact]
    public async Task Lists_jobs_and_shows_one_with_its_tail()
    {
        Job job = Runner.On("echo", "hello", 0, "hello").Start(new ProcessSpec("echo", ["hello"], "/"));

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/jobs", TestContext.Current.CancellationToken);
        JsonElement one = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.Id}", TestContext.Current.CancellationToken);

        list.EnumerateArray().Select(j => j.GetProperty("id").GetString()).ShouldContain(job.Id);
        one.GetProperty("summary").GetProperty("commandLine").GetString().ShouldBe("echo hello");
        one.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Exited");
        one.GetProperty("lines").EnumerateArray().Single().GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task An_unknown_job_is_a_404_problem()
    {
        HttpResponseMessage response = await client.GetAsync("/api/jobs/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Streams_buffered_lines_then_live_lines_then_exit()
    {
        Job job = Runner.OnLongRunning("tail", "x", "first").Start(new ProcessSpec("tail", ["x"], "/"));

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/jobs/{job.Id}/stream");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        job.Append(OutputStream.Stdout, "second");
        job.MarkExited(0);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldContain("event: line");
        body.ShouldContain("id: 0");
        body.ShouldContain("\"text\":\"first\"");
        body.ShouldContain("id: 1");
        body.ShouldContain("\"text\":\"second\"");
        body.ShouldContain("event: exited");
        body.ShouldContain("\"exitCode\":0");
    }

    [Fact]
    public async Task Resumes_after_the_given_sequence()
    {
        Job job = Runner.On("echo", "three", 0, "a", "b", "c").Start(new ProcessSpec("echo", ["three"], "/"));

        string body = await client.GetStringAsync($"/api/jobs/{job.Id}/stream?after=1", TestContext.Current.CancellationToken);

        body.ShouldNotContain("\"text\":\"a\"");
        body.ShouldNotContain("\"text\":\"b\"");
        body.ShouldContain("\"text\":\"c\"");
    }

    [Fact]
    public async Task Resumes_from_the_last_event_id_header()
    {
        Job job = Runner.On("echo", "three", 0, "a", "b", "c").Start(new ProcessSpec("echo", ["three"], "/"));
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/jobs/{job.Id}/stream");
        request.Headers.Add("Last-Event-ID", "0");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldNotContain("\"text\":\"a\"");
        body.ShouldContain("\"text\":\"b\"");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~JobEndpointTests"`
Expected: FAIL, `FakeProcessRunner` is not registered (`InvalidOperationException: No service for type`).

- [ ] **Step 3: Write `JobStream` and `JobEndpoints`**

`src/Admin.Host/Jobs/JobStream.cs`:

```csharp
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Admin.Host.Jobs;

/// <summary>
/// One job as Server-Sent Events: a <c>line</c> event per output line whose
/// id is the sequence number, so a reconnecting EventSource resumes from the
/// ring buffer, then one <c>exited</c> event.
/// </summary>
public static class JobStream
{
    public static async IAsyncEnumerable<SseItem<string>> Events(Job job, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (OutputLine line in job.Follow(afterSequence, cancellationToken))
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(line, JsonSerializerOptions.Web), "line")
            {
                EventId = line.Sequence.ToString(CultureInfo.InvariantCulture),
            };
        }

        int exitCode = await job.Completion.WaitAsync(cancellationToken);

        yield return new SseItem<string>(JsonSerializer.Serialize(new { exitCode }, JsonSerializerOptions.Web), "exited");
    }
}
```

`src/Admin.Host/Jobs/JobEndpoints.cs`:

```csharp
using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Jobs;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", (JobRegistry registry) => TypedResults.Ok(registry.All().Select(JobSummary.Of).ToList()));

        app.MapGet("/api/jobs/{id}", Results<Ok<JobView>, ProblemHttpResult> (string id, JobRegistry registry) =>
            registry.Find(id) is Job job
                ? TypedResults.Ok(new JobView(JobSummary.Of(job), job.Tail(200)))
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job", detail: id));

        app.MapGet("/api/jobs/{id}/stream", IResult (string id, long? after, HttpContext context, JobRegistry registry, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is not Job job)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job", detail: id);
            }

            long resumeAfter = after ?? ParseLastEventId(context) ?? -1;

            return TypedResults.ServerSentEvents(JobStream.Events(job, resumeAfter, cancellationToken));
        });

        return app;
    }

    private static long? ParseLastEventId(HttpContext context)
    {
        string? header = context.Request.Headers["Last-Event-ID"].FirstOrDefault();

        return long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }
}

public sealed record JobView(JobSummary Summary, IReadOnlyList<OutputLine> Lines);
```

- [ ] **Step 4: Register the services in `Program.cs`**

Replace the block between `builder.Services.AddProblemDetails();` and `WebApplication app = builder.Build();` with:

```csharp
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobRegistry>();

// Both runners are registered; which one IProcessRunner resolves to is
// decided from options at resolve time, for the same reason RepoPaths is.
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<FakeProcessRunner>();
builder.Services.AddSingleton<IProcessRunner>(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? sp.GetRequiredService<FakeProcessRunner>()
        : sp.GetRequiredService<ProcessRunner>());
```

Add `using Admin.Host.Fakes;` and `using Admin.Host.Jobs;` at the top, and `app.MapJobs();` after `app.MapConfig();`.

- [ ] **Step 5: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: all pass (20).

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Host tests/Admin.Host.Tests
git commit -m "feat(host): job endpoints and the SSE line stream"
```

---

### Task 6: ComposeService and the `ps` parser

**Files:**
- Create: `src/Admin.Host/Compose/ServiceStatus.cs`, `src/Admin.Host/Compose/ComposeStatus.cs`, `src/Admin.Host/Compose/ComposePsParser.cs`, `src/Admin.Host/Compose/ComposeService.cs`
- Test: `tests/Admin.Host.Tests/Compose/ComposePsParserTests.cs`, `tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `RepoPaths`, `Job`.
- Produces:
  - `record ServiceStatus(string Service, string State, string? Health, int? ExitCode, IReadOnlyList<int> PublishedPorts)`
  - `record ComposeStatus(bool Reachable, string? Error, IReadOnlyList<ServiceStatus> Services)` with `static Up(services)` and `static Unreachable(error)`
  - `static class ComposePsParser { static IReadOnlyList<ServiceStatus> Parse(IEnumerable<string> stdoutLines); }` accepting NDJSON (one object per line, Compose v2.21+) and a single JSON array (older Compose)
  - `class ComposeService(IProcessRunner runner, RepoPaths paths)`: `Job Up()`, `Job Down(bool wipeVolumes)`, `Job FollowLogs(IReadOnlyList<string> services)`, `Job Exec(string service, params string[] args)`, `Task<ComposeStatus> PsAsync(CancellationToken)`.
  - Every command is `docker compose -f <ComposeFile> ...` run in `BackendDir`. `Ps` is `ps -a --format json`, so exited migrators are listed.

- [ ] **Step 1: Write the failing parser tests**

`tests/Admin.Host.Tests/Compose/ComposePsParserTests.cs`:

```csharp
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
        services[0].ShouldBe(new ServiceStatus("gateway", "running", "healthy", 0, [5000]));
        services[1].ShouldBe(new ServiceStatus("catalog-migrator", "exited", null, 0, []));
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
}
```

- [ ] **Step 2: Write the failing service tests**

`tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs`:

```csharp
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Compose;

public sealed class ComposeServiceTests
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private readonly FakeProcessRunner runner = new(new JobRegistry(new FakeTimeProvider()));

    private ComposeService Service => new(runner, Paths);

    [Fact]
    public void Up_runs_compose_up_detached_and_waits()
    {
        Service.Up();

        ProcessSpec spec = runner.Started.Single();
        spec.FileName.ShouldBe("docker");
        spec.Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "up", "-d", "--wait"]);
        spec.WorkingDirectory.ShouldBe("/repo/backend");
    }

    [Fact]
    public void Down_wipes_volumes_only_when_asked()
    {
        Service.Down(wipeVolumes: false);
        Service.Down(wipeVolumes: true);

        runner.Started[0].Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "down"]);
        runner.Started[1].Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "down", "-v"]);
    }

    [Fact]
    public void FollowLogs_tails_the_named_services()
    {
        Service.FollowLogs(["gateway", "web-bff"]);

        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "logs", "-f", "--tail", "200", "gateway", "web-bff"]);
    }

    [Fact]
    public void Exec_runs_inside_the_service_without_a_tty()
    {
        Service.Exec("rabbitmq", "rabbitmqctl", "list_queues");

        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues"]);
    }

    [Fact]
    public async Task Ps_parses_the_services_when_docker_answers()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps -a --format json", 0,
            """{"Name":"commerce-gateway-1","Service":"gateway","State":"running","Health":"healthy","ExitCode":0,"Publishers":[]}""");

        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeTrue();
        status.Services.Single().Service.ShouldBe("gateway");
    }

    [Fact]
    public async Task Ps_is_unreachable_with_the_last_stderr_line_when_docker_fails()
    {
        // The fake writes to stdout only; a non-zero exit with no stderr still reports the exit code.
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps", 1);

        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldBe("docker compose ps exited with 1");
        status.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ps_is_unreachable_when_docker_is_not_installed()
    {
        // No script: the fake exits 127 with a stderr line, the same shape as a missing executable.
        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldStartWith("fake: no script for docker compose");
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Compose"`
Expected: build FAILS, `ComposePsParser` not found.

- [ ] **Step 4: Write the Compose types**

`src/Admin.Host/Compose/ServiceStatus.cs`:

```csharp
namespace Admin.Host.Compose;

/// <summary>One Compose service as <c>docker compose ps</c> reports it. Health is null when the service declares no healthcheck.</summary>
public sealed record ServiceStatus(string Service, string State, string? Health, int? ExitCode, IReadOnlyList<int> PublishedPorts);
```

`src/Admin.Host/Compose/ComposeStatus.cs`:

```csharp
namespace Admin.Host.Compose;

/// <summary>The stack as a whole. A Docker that does not answer is a state, not an exception (spec §9).</summary>
public sealed record ComposeStatus(bool Reachable, string? Error, IReadOnlyList<ServiceStatus> Services)
{
    public static ComposeStatus Up(IReadOnlyList<ServiceStatus> services) => new(true, null, services);

    public static ComposeStatus Unreachable(string error) => new(false, error, []);
}
```

`src/Admin.Host/Compose/ComposePsParser.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Admin.Host.Compose;

/// <summary>
/// <c>docker compose ps --format json</c> prints one object per line since
/// Compose 2.21 and a single array before that. Both are accepted.
/// </summary>
public static class ComposePsParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ServiceStatus> Parse(IEnumerable<string> stdoutLines)
    {
        List<ServiceStatus> services = [];

        foreach (string raw in stdoutLines)
        {
            string line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                services.AddRange(JsonSerializer.Deserialize<List<PsRow>>(line, Options)!.Select(ToStatus));
            }
            else
            {
                services.Add(ToStatus(JsonSerializer.Deserialize<PsRow>(line, Options)!));
            }
        }

        return services;
    }

    private static ServiceStatus ToStatus(PsRow row) => new(
        row.Service,
        row.State,
        string.IsNullOrEmpty(row.Health) ? null : row.Health,
        row.ExitCode,
        (row.Publishers ?? []).Where(p => p.PublishedPort > 0).Select(p => p.PublishedPort).Distinct().ToList());

    private sealed record PsRow(
        string Service,
        string State,
        string? Health,
        int? ExitCode,
        [property: JsonPropertyName("Publishers")] List<Publisher>? Publishers);

    private sealed record Publisher(int PublishedPort);
}
```

`src/Admin.Host/Compose/ComposeService.cs`:

```csharp
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Compose;

/// <summary>
/// Every Compose command the console runs, built from <see cref="RepoPaths"/>
/// and run in the backend clone: the exact lines run-locally.md gives.
/// </summary>
public sealed class ComposeService(IProcessRunner runner, RepoPaths paths)
{
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(30);

    public Job Up() => Run("up", "-d", "--wait");

    public Job Down(bool wipeVolumes) => wipeVolumes ? Run("down", "-v") : Run("down");

    public Job FollowLogs(IReadOnlyList<string> services) => Run(["logs", "-f", "--tail", "200", .. services]);

    public Job Exec(string service, params string[] args) => Run(["exec", "-T", service, .. args]);

    public async Task<ComposeStatus> PsAsync(CancellationToken cancellationToken)
    {
        Job job = Run("ps", "-a", "--format", "json");
        int exitCode;

        try
        {
            exitCode = await job.Completion.WaitAsync(PsTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await runner.StopAsync(job, cancellationToken);

            return ComposeStatus.Unreachable("docker compose ps did not answer within 30 seconds");
        }

        IReadOnlyList<OutputLine> lines = job.Since(-1);

        if (exitCode != 0)
        {
            string? lastError = lines.LastOrDefault(l => l.Stream == OutputStream.Stderr)?.Text;

            return ComposeStatus.Unreachable(lastError ?? $"docker compose ps exited with {exitCode}");
        }

        return ComposeStatus.Up(ComposePsParser.Parse(lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)));
    }

    private Job Run(params string[] args) => runner.Start(new ProcessSpec("docker", ["compose", "-f", paths.ComposeFile, .. args], paths.BackendDir));
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Compose"`
Expected: 10 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Host/Compose tests/Admin.Host.Tests/Compose
git commit -m "feat(host): compose commands and the ps parser"
```

---

### Task 7: PlatformProbe and the Stack endpoints

**Files:**
- Create: `src/Admin.Host/Stack/PlatformProbe.cs`, `src/Admin.Host/Stack/StackEndpoints.cs`
- Modify: `src/Admin.Host/Program.cs` (register `ComposeService`, `PlatformProbe` via `AddHttpClient`; call `MapStack()`)
- Test: `tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs`, `tests/Admin.Host.Tests/Stack/StackEndpointTests.cs`

**Interfaces:**
- Consumes: `ComposeService`, `ComposeStatus`, `JobSummary`, `AdminOptions`, `FakeProcessRunner`.
- Produces:
  - `record Reachability(string Name, string Url, bool Up, int? Status)`
  - `class PlatformProbe(HttpClient http, IOptions<AdminOptions> options)` with `Task<IReadOnlyList<Reachability>> ProbeAsync(CancellationToken)`; targets in order: `gateway` `/health/ready`, `catalog` `/health/ready`, `ordering` `/health/ready`, `bff` `/health/ready`, `keycloak` `/realms/{Realm}`, `grafana` `/api/health`, `client` `/`. A 2-second timeout per target; any exception is `Up = false, Status = null`.
  - `GET /api/stack` → `StackView(ComposeStatus Backend, IReadOnlyList<Reachability> Reachability)`
  - `POST /api/stack/backend/up` → `JobSummary` (202)
  - `POST /api/stack/backend/down` body `DownRequest(bool WipeVolumes, string? Confirm)` → `JobSummary` (202); when `WipeVolumes` and `Confirm != "down -v"` → 400 problem titled `Confirmation required`.
  - `POST /api/logs/follow` body `FollowRequest(IReadOnlyList<string> Services)` → `JobSummary` (202)
  - `Program` registers `PlatformProbe` with `builder.Services.AddHttpClient<PlatformProbe>()`. Task 8 swaps the primary handler in FakePlatform mode; the probe tests here use their own handler.

- [ ] **Step 1: Write the failing probe tests**

`tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs`:

```csharp
using System.Net;
using Admin.Host.Config;
using Admin.Host.Stack;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class PlatformProbeTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static PlatformProbe Probe(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new ScriptedHandler(respond)), Options.Create(new AdminOptions()));

    [Fact]
    public async Task Reports_each_surface_in_order_with_its_status()
    {
        PlatformProbe probe = Probe(request => request.RequestUri!.Port == 3000
            ? throw new HttpRequestException("refused")
            : new HttpResponseMessage(request.RequestUri.Port == 8080 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));

        IReadOnlyList<Reachability> result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Select(r => r.Name).ShouldBe(["gateway", "catalog", "ordering", "bff", "keycloak", "grafana", "client"]);
        result.Single(r => r.Name == "gateway").Url.ShouldBe("http://localhost:5000/health/ready");
        result.Single(r => r.Name == "keycloak").ShouldBe(new Reachability("keycloak", "http://localhost:8080/realms/commerce", true, 200));
        result.Single(r => r.Name == "grafana").ShouldBe(new Reachability("grafana", "http://localhost:3000/api/health", false, null));
        result.Single(r => r.Name == "gateway").Up.ShouldBeFalse();
        result.Single(r => r.Name == "gateway").Status.ShouldBe(503);
    }
}
```

- [ ] **Step 2: Write the failing endpoint tests**

`tests/Admin.Host.Tests/Stack/StackEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class StackEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public StackEndpointTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private FakeProcessRunner Runner => factory.Services.GetRequiredService<FakeProcessRunner>();

    [Fact]
    public async Task Stack_reports_compose_services_and_reachability()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

        stack.GetProperty("backend").GetProperty("reachable").GetBoolean().ShouldBeTrue();
        stack.GetProperty("backend").GetProperty("services").EnumerateArray()
            .Select(s => s.GetProperty("service").GetString()).ShouldContain("gateway");
        stack.GetProperty("reachability").EnumerateArray().Count().ShouldBe(7);
    }

    [Fact]
    public async Task Up_starts_a_compose_up_job()
    {
        HttpResponseMessage response = await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldEndWith("up -d --wait");
        Runner.Started.ShouldContain(s => s.Arguments.Contains("up"));
    }

    [Fact]
    public async Task Down_without_wipe_needs_no_confirmation()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = false }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Runner.Started.Last().Arguments.ShouldNotContain("-v");
    }

    [Fact]
    public async Task Wiping_volumes_without_the_typed_confirmation_is_refused()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = true, confirm = "yes" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().ShouldBe("Confirmation required");
        Runner.Started.ShouldNotContain(s => s.Arguments.Contains("-v"));
    }

    [Fact]
    public async Task Wiping_volumes_with_the_typed_confirmation_runs_down_v()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = true, confirm = "down -v" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Runner.Started.Last().Arguments.TakeLast(2).ShouldBe(["down", "-v"]);
    }

    [Fact]
    public async Task Follow_logs_starts_a_running_job_for_the_named_services()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/logs/follow", new { services = new[] { "gateway" } }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("state").GetString().ShouldBe("Running");
        job.GetProperty("commandLine").GetString().ShouldEndWith("logs -f --tail 200 gateway");
    }
}
```

Two of these (`Stack_reports_compose_services_and_reachability`, `Follow_logs_starts_a_running_job_for_the_named_services`) depend on the FakePlatform scripts from Task 8 and fail until it lands with the fake's exit 127. That is expected; Task 8 step 6 runs them again.

- [ ] **Step 3: Run to verify the build fails**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Stack"`
Expected: build FAILS, `PlatformProbe` not found.

- [ ] **Step 4: Write the probe**

`src/Admin.Host/Stack/PlatformProbe.cs`:

```csharp
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Stack;

public sealed record Reachability(string Name, string Url, bool Up, int? Status);

/// <summary>
/// Whether each HTTP surface on the workstation answers. A refused connection
/// is <c>Up = false</c> with no status; a 503 from a readiness check is
/// <c>Up = false</c> with the status, because the two mean different things
/// on the Stack screen.
/// </summary>
public sealed class PlatformProbe(HttpClient http, IOptions<AdminOptions> options)
{
    private static readonly TimeSpan PerTarget = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<Reachability>> ProbeAsync(CancellationToken cancellationToken)
    {
        AdminOptions o = options.Value;

        (string Name, string Url)[] targets =
        [
            ("gateway", $"{o.GatewayUrl}/health/ready"),
            ("catalog", $"{o.CatalogUrl}/health/ready"),
            ("ordering", $"{o.OrderingUrl}/health/ready"),
            ("bff", $"{o.BffUrl}/health/ready"),
            ("keycloak", $"{o.KeycloakUrl}/realms/{o.Realm}"),
            ("grafana", $"{o.GrafanaUrl}/api/health"),
            ("client", $"{o.ClientUrl}/"),
        ];

        return await Task.WhenAll(targets.Select(t => ProbeOneAsync(t.Name, t.Url, cancellationToken)));
    }

    private async Task<Reachability> ProbeOneAsync(string name, string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerTarget);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return new Reachability(name, url, response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new Reachability(name, url, false, null);
        }
    }
}
```

- [ ] **Step 5: Write the Stack endpoints**

`src/Admin.Host/Stack/StackEndpoints.cs`:

```csharp
using Admin.Host.Compose;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Stack;

public static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStack(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stack", async (ComposeService compose, PlatformProbe probe, CancellationToken cancellationToken) =>
        {
            Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
            Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);

            return TypedResults.Ok(new StackView(await backend, await reachability));
        });

        app.MapPost("/api/stack/backend/up", (ComposeService compose) => TypedResults.Accepted((string?)null, JobSummary.Of(compose.Up())));

        app.MapPost("/api/stack/backend/down", Results<Accepted<JobSummary>, ProblemHttpResult> (DownRequest request, ComposeService compose) =>
        {
            if (request.WipeVolumes && request.Confirm != "down -v")
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Confirmation required",
                    detail: "Wiping volumes destroys databases and broker state. Send confirm: \"down -v\".");
            }

            return TypedResults.Accepted((string?)null, JobSummary.Of(compose.Down(request.WipeVolumes)));
        });

        app.MapPost("/api/logs/follow", (FollowRequest request, ComposeService compose) =>
            TypedResults.Accepted((string?)null, JobSummary.Of(compose.FollowLogs(request.Services ?? []))));

        return app;
    }
}

public sealed record StackView(ComposeStatus Backend, IReadOnlyList<Reachability> Reachability);

public sealed record DownRequest(bool WipeVolumes, string? Confirm);

public sealed record FollowRequest(IReadOnlyList<string>? Services);
```

- [ ] **Step 6: Register in `Program.cs`**

After the `IProcessRunner` registration block add:

```csharp
builder.Services.AddSingleton<ComposeService>();
builder.Services.AddHttpClient<PlatformProbe>();
```

Add `using Admin.Host.Compose;` and `using Admin.Host.Stack;`, and `app.MapStack();` after `app.MapJobs();`.

- [ ] **Step 7: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Stack"`
Expected: 5 passed, 2 failed (the two named under Step 2), each on `reachable: false` or `state: Exited` from the unscripted fake.

- [ ] **Step 8: Commit**

```bash
git add src/Admin.Host tests/Admin.Host.Tests
git commit -m "feat(host): stack endpoints, platform probe and the down -v confirmation"
```

---

### Task 8: FakePlatform mode

**Files:**
- Create: `src/Admin.Host/Fakes/FakePlatformScripts.cs`, `src/Admin.Host/Fakes/FakePlatformHandler.cs`, `src/Admin.Host/Fakes/fixtures/compose-ps.jsonl`
- Modify: `src/Admin.Host/Admin.Host.csproj` (embed the fixture), `src/Admin.Host/Program.cs` (script the fake runner; fake HTTP handler)
- Test: `tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs`

**Interfaces:**
- Consumes: `FakeProcessRunner`, `PlatformProbe`, `RepoPaths`.
- Produces:
  - `static class FakePlatformScripts { static FakeProcessRunner Script(FakeProcessRunner runner, string composeFile); static IReadOnlyList<string> ComposePsLines(); }`
  - `class FakePlatformHandler : HttpMessageHandler` answering 200 with a small JSON body for every surface.
  - Scripts: `ps -a --format json` → the fixture rows, exit 0; `up -d --wait` → 6 progress lines, exit 0; `down -v` → 6 lines, exit 0; `down` → 4 lines, exit 0; `logs -f` → long-running with 12 lines in Compose's `service | message` shape, two of them carrying `CorrelationId=fake-corr-0001` so the Logs screen's highlight has something to find.

- [ ] **Step 1: Write the fixture and embed it**

`src/Admin.Host/Fakes/fixtures/compose-ps.jsonl` (one object per line, the thirteen services in the order Compose lists them):

```
{"Name":"commerce-sql-1","Service":"sql","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":1433,"PublishedPort":1433,"Protocol":"tcp"}]}
{"Name":"commerce-redis-cache-1","Service":"redis-cache","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":6379,"PublishedPort":6379,"Protocol":"tcp"}]}
{"Name":"commerce-redis-coordination-1","Service":"redis-coordination","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":6379,"PublishedPort":6380,"Protocol":"tcp"}]}
{"Name":"commerce-rabbitmq-1","Service":"rabbitmq","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":5672,"PublishedPort":5672,"Protocol":"tcp"},{"URL":"127.0.0.1","TargetPort":15672,"PublishedPort":15672,"Protocol":"tcp"}]}
{"Name":"commerce-keycloak-1","Service":"keycloak","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":8080,"Protocol":"tcp"}]}
{"Name":"commerce-otel-collector-1","Service":"otel-collector","State":"running","Health":"","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":4317,"PublishedPort":4317,"Protocol":"tcp"},{"URL":"127.0.0.1","TargetPort":4318,"PublishedPort":4318,"Protocol":"tcp"}]}
{"Name":"commerce-grafana-1","Service":"grafana","State":"running","Health":"","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":3000,"PublishedPort":3000,"Protocol":"tcp"}]}
{"Name":"commerce-catalog-migrator-1","Service":"catalog-migrator","State":"exited","Health":"","ExitCode":0,"Publishers":[]}
{"Name":"commerce-catalog-api-1","Service":"catalog-api","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":5102,"Protocol":"tcp"}]}
{"Name":"commerce-ordering-migrator-1","Service":"ordering-migrator","State":"exited","Health":"","ExitCode":0,"Publishers":[]}
{"Name":"commerce-ordering-api-1","Service":"ordering-api","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":5101,"Protocol":"tcp"}]}
{"Name":"commerce-gateway-1","Service":"gateway","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":5000,"Protocol":"tcp"}]}
{"Name":"commerce-web-bff-1","Service":"web-bff","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":5200,"Protocol":"tcp"}]}
```

In `src/Admin.Host/Admin.Host.csproj` add:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Fakes\fixtures\compose-ps.jsonl" LogicalName="compose-ps.jsonl" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakePlatformTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public FakePlatformTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public void The_fixture_lists_thirteen_services()
    {
        FakePlatformScripts.ComposePsLines().Count.ShouldBe(13);
    }

    [Fact]
    public async Task Every_surface_is_reachable_in_fake_mode()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

        stack.GetProperty("reachability").EnumerateArray().ShouldAllBe(r => r.GetProperty("up").GetBoolean());
        stack.GetProperty("backend").GetProperty("services").EnumerateArray().Count().ShouldBe(13);
    }

    [Fact]
    public async Task Up_produces_output_and_exits_zero()
    {
        HttpResponseMessage started = await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

        job.GetProperty("summary").GetProperty("exitCode").GetInt32().ShouldBe(0);
        job.GetProperty("lines").EnumerateArray().Count().ShouldBe(6);
    }

    [Fact]
    public async Task Logs_follow_carries_a_correlation_id_line()
    {
        HttpResponseMessage started = await client.PostAsJsonAsync("/api/logs/follow", new { services = Array.Empty<string>() }, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

        job.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Running");
        job.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ShouldContain(t => t.Contains("CorrelationId=fake-corr-0001"));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~FakePlatform"`
Expected: build FAILS, `FakePlatformScripts` not found.

- [ ] **Step 4: Write the scripts and the handler**

`src/Admin.Host/Fakes/FakePlatformScripts.cs`:

```csharp
using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>The recorded docker outputs FakePlatform mode replays. Exact text is not a contract; the shapes are.</summary>
public static class FakePlatformScripts
{
    public static FakeProcessRunner Script(FakeProcessRunner runner, string composeFile)
    {
        string prefix = $"compose -f {composeFile} ";

        return runner
            .On("docker", prefix + "ps -a --format json", 0, [.. ComposePsLines()])
            .On("docker", prefix + "up -d --wait", 0,
                " Network commerce_default  Created",
                " Container commerce-sql-1  Healthy",
                " Container commerce-rabbitmq-1  Healthy",
                " Container commerce-keycloak-1  Healthy",
                " Container commerce-catalog-api-1  Healthy",
                " Container commerce-gateway-1  Healthy")
            .On("docker", prefix + "down -v", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Volume commerce_sql-data  Removed",
                " Volume commerce_rabbit-data  Removed",
                " Network commerce_default  Removed")
            .On("docker", prefix + "down", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Network commerce_default  Removed")
            .OnLongRunning("docker", prefix + "logs -f",
                "gateway       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
                "catalog-api   | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
                "ordering-api  | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
                "web-bff       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
                "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0001",
                "catalog-api   | info: Catalog.Api.Endpoints[1] Listed 20 products CorrelationId=fake-corr-0001",
                "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0002",
                "catalog-api   | info: Catalog.Api.Endpoints[2] Published product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f CorrelationId=fake-corr-0002",
                "catalog-api   | info: Common.Infrastructure.Outbox.OutboxDispatcher[10] Dispatched PriceChanged to catalog-events CorrelationId=fake-corr-0002",
                "ordering-api  | info: MassTransit[0] Consumed PriceChanged on ordering-catalog-events CorrelationId=fake-corr-0002",
                "ordering-api  | info: Ordering.Application.Projections[3] Priced product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f at 19.99 EUR",
                "rabbitmq      | 2026-09-14 07:00:00.000 [info] <0.1.0> connection accepted from ordering-api");
    }

    public static IReadOnlyList<string> ComposePsLines()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("compose-ps.jsonl")!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
```

The `down -v` script is registered before `down` because the fake takes the first prefix match and `down` is a prefix of `down -v`.

`src/Admin.Host/Fakes/FakePlatformHandler.cs`:

```csharp
using System.Net;
using System.Net.Mime;
using System.Text;

namespace Admin.Host.Fakes;

/// <summary>Every workstation surface answers 200 in FakePlatform mode. Later phases add the Keycloak, OpenAPI and Grafana bodies here.</summary>
public sealed class FakePlatformHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => """{"status":"Healthy"}""",
            "/api/health" => """{"database":"ok","version":"fake"}""",
            _ => """{"fake":true}""",
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
        });
    }
}
```

- [ ] **Step 5: Wire FakePlatform in `Program.cs`**

Replace Task 5's `builder.Services.AddSingleton<FakeProcessRunner>();` line with a scripted instance, and Task 7's `builder.Services.AddHttpClient<PlatformProbe>();` line with one whose primary handler is chosen from options:

```csharp
builder.Services.AddSingleton(sp => FakePlatformScripts.Script(
    new FakeProcessRunner(sp.GetRequiredService<JobRegistry>()),
    sp.GetRequiredService<RepoPaths>().ComposeFile));
```

```csharp
builder.Services.AddHttpClient<PlatformProbe>().ConfigurePrimaryHttpMessageHandler(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? new FakePlatformHandler()
        : new HttpClientHandler());
```

The `IProcessRunner` factory from Task 5 already picks the fake when `FakePlatform` is true; it now receives the scripted instance.

- [ ] **Step 6: Run every test**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: all pass, including the two Task 7 tests that were waiting on this.

- [ ] **Step 7: Run the host in fake mode and exercise it by hand**

Run: `dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true`
Then, in another shell:

```bash
curl -s http://127.0.0.1:5300/api/stack | head -c 400
curl -s -X POST http://127.0.0.1:5300/api/logs/follow -H 'content-type: application/json' -d '{"services":[]}'
curl -N http://127.0.0.1:5300/api/jobs/<id from the previous line>/stream
```

Expected: the stack JSON lists thirteen services; the follow job is `Running`; the stream prints twelve `event: line` records and stays open until Ctrl+C.

- [ ] **Step 8: Commit**

```bash
git add src/Admin.Host tests/Admin.Host.Tests
git commit -m "feat(host): FakePlatform mode with recorded compose output"
```

---

### Task 9: Angular workspace, shell and the host clients

**Files:**
- Create: the Angular workspace under `src/Admin.Web/` (generated), `src/Admin.Web/proxy.conf.json`, `src/Admin.Web/.prettierrc`
- Create: `src/Admin.Web/src/app/core/host/host-types.ts`, `host-client.ts`, `host-client.spec.ts`, `sse-client.ts`, `sse-client.spec.ts`
- Modify: `src/Admin.Web/angular.json` (ports, proxy, output path), `src/Admin.Web/src/app/app.ts`, `app.html`, `app.routes.ts`, `app.config.ts`

**Interfaces:**
- Produces:
  - `host-types.ts`: `JobState = 'Running' | 'Exited'`, `JobSummary { id; commandLine; state: JobState; exitCode: number | null; startedAt: string }`, `OutputLine { sequence: number; at: string; stream: 'Stdout' | 'Stderr'; text: string }`, `JobView { summary: JobSummary; lines: OutputLine[] }`, `ServiceStatus { service; state; health: string | null; exitCode: number | null; publishedPorts: number[] }`, `ComposeStatus { reachable: boolean; error: string | null; services: ServiceStatus[] }`, `Reachability { name; url; up: boolean; status: number | null }`, `StackView { backend: ComposeStatus; reachability: Reachability[] }`, `ConfigView { backendDir; frontendDir; composeFile; fakePlatform: boolean; urls: { gateway; catalog; ordering; bff; keycloak; grafana; client } }`
  - `HostClient` (providedIn root): `config(): Observable<ConfigView>`, `stack(): Observable<StackView>`, `backendUp(): Observable<JobSummary>`, `backendDown(wipeVolumes: boolean, confirm?: string): Observable<JobSummary>`, `followLogs(services: string[]): Observable<JobSummary>`, `job(id: string): Observable<JobView>`
  - `SseClient` (providedIn root): `follow(jobId: string, after = -1): Observable<JobEvent>` where `JobEvent = { kind: 'line'; line: OutputLine } | { kind: 'exited'; exitCode: number }`; completes after `exited`; closes the EventSource on unsubscribe.
  - Routes: `''` redirects to `stack`; `stack` and `logs` lazy-load the pages from Tasks 10 and 11. Until those exist the routes point at two placeholder components created in this task and replaced in place later.

- [ ] **Step 1: Generate the workspace**

From the repo root:

```bash
npx --yes @angular/cli@22.1.8 new admin-web --directory src/Admin.Web --style css --ssr false --skip-git --package-manager npm --strict
cd src/Admin.Web
npx ng add angular-eslint --skip-confirmation
npm install --save-dev prettier@3.9.6 @playwright/test@1.63.0
cp ../../../blueprint-frontend/.prettierrc .prettierrc
```

Expected: `npm test -- --watch=false` reports the generated `App` spec passing under Vitest; `npm run lint` passes.

- [ ] **Step 2: Set ports, proxy and the build output**

`src/Admin.Web/proxy.conf.json`:

```json
{
  "/api": {
    "target": "http://127.0.0.1:5300",
    "secure": false,
    "changeOrigin": false
  }
}
```

In `angular.json`, under `projects.admin-web.architect`:
- `serve.options` becomes `{ "port": 5301, "host": "127.0.0.1", "proxyConfig": "proxy.conf.json" }`
- `build.options.outputPath` becomes `{ "base": "../Admin.Host/wwwroot", "browser": "" }`

Add to `package.json` scripts: `"e2e": "playwright test"`, and change `"test"` to `"ng test --watch=false"`.

- [ ] **Step 3: Write the failing `HostClient` spec**

`src/Admin.Web/src/app/core/host/host-client.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HostClient } from './host-client';

describe('HostClient', () => {
  let client: HostClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    client = TestBed.inject(HostClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the stack', () => {
    let seen: unknown;
    client.stack().subscribe((s) => (seen = s));

    const req = http.expectOne('/api/stack');
    expect(req.request.method).toBe('GET');
    req.flush({ backend: { reachable: true, error: null, services: [] }, reachability: [] });

    expect(seen).toEqual({ backend: { reachable: true, error: null, services: [] }, reachability: [] });
  });

  it('sends the typed confirmation with a wipe', () => {
    client.backendDown(true, 'down -v').subscribe();

    const req = http.expectOne('/api/stack/backend/down');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ wipeVolumes: true, confirm: 'down -v' });
    req.flush({ id: 'j1', commandLine: 'docker compose down -v', state: 'Running', exitCode: null, startedAt: '' });
  });

  it('follows logs for the named services', () => {
    client.followLogs(['gateway']).subscribe();

    const req = http.expectOne('/api/logs/follow');
    expect(req.request.body).toEqual({ services: ['gateway'] });
    req.flush({ id: 'j2', commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' });
  });
});
```

- [ ] **Step 4: Run it to verify it fails**

Run: `npm test`
Expected: FAIL, cannot resolve `./host-client`.

- [ ] **Step 5: Write the types and the client**

`src/Admin.Web/src/app/core/host/host-types.ts`:

```ts
export type JobState = 'Running' | 'Exited';

export interface JobSummary {
  id: string;
  commandLine: string;
  state: JobState;
  exitCode: number | null;
  startedAt: string;
}

export interface OutputLine {
  sequence: number;
  at: string;
  stream: 'Stdout' | 'Stderr';
  text: string;
}

export interface JobView {
  summary: JobSummary;
  lines: OutputLine[];
}

export interface ServiceStatus {
  service: string;
  state: string;
  health: string | null;
  exitCode: number | null;
  publishedPorts: number[];
}

export interface ComposeStatus {
  reachable: boolean;
  error: string | null;
  services: ServiceStatus[];
}

export interface Reachability {
  name: string;
  url: string;
  up: boolean;
  status: number | null;
}

export interface StackView {
  backend: ComposeStatus;
  reachability: Reachability[];
}

export interface ConfigView {
  backendDir: string;
  frontendDir: string;
  composeFile: string;
  fakePlatform: boolean;
  urls: {
    gateway: string;
    catalog: string;
    ordering: string;
    bff: string;
    keycloak: string;
    grafana: string;
    client: string;
  };
}
```

`src/Admin.Web/src/app/core/host/host-client.ts`:

```ts
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigView, JobSummary, JobView, StackView } from './host-types';

/** Every call the SPA makes; there is no other origin (spec §3, §7). */
@Injectable({ providedIn: 'root' })
export class HostClient {
  private readonly http = inject(HttpClient);

  config(): Observable<ConfigView> {
    return this.http.get<ConfigView>('/api/config');
  }

  stack(): Observable<StackView> {
    return this.http.get<StackView>('/api/stack');
  }

  backendUp(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/backend/up', null);
  }

  backendDown(wipeVolumes: boolean, confirm?: string): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/backend/down', { wipeVolumes, confirm });
  }

  followLogs(services: string[]): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/logs/follow', { services });
  }

  job(id: string): Observable<JobView> {
    return this.http.get<JobView>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
```

- [ ] **Step 6: Run the spec**

Run: `npm test`
Expected: `HostClient` 3 passed.

- [ ] **Step 7: Write the failing `SseClient` spec**

`src/Admin.Web/src/app/core/host/sse-client.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { JobEvent, SseClient } from './sse-client';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, (e: MessageEvent) => void>();
  closed = false;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (e: MessageEvent) => void): void {
    this.listeners.set(type, listener);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data: unknown, lastEventId = ''): void {
    this.listeners.get(type)?.(new MessageEvent(type, { data: JSON.stringify(data), lastEventId }));
  }
}

describe('SseClient', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('opens the job stream and maps line and exited events', () => {
    const client = TestBed.inject(SseClient);
    const seen: JobEvent[] = [];
    let completed = false;

    client.follow('j1', 4).subscribe({ next: (e) => seen.push(e), complete: () => (completed = true) });

    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/jobs/j1/stream?after=4');
    source.emit('line', { sequence: 5, at: '', stream: 'Stdout', text: 'hello' }, '5');
    source.emit('exited', { exitCode: 0 });

    expect(seen).toEqual([
      { kind: 'line', line: { sequence: 5, at: '', stream: 'Stdout', text: 'hello' } },
      { kind: 'exited', exitCode: 0 },
    ]);
    expect(completed).toBe(true);
    expect(source.closed).toBe(true);
  });

  it('closes the source on unsubscribe', () => {
    const client = TestBed.inject(SseClient);

    const sub = client.follow('j2').subscribe();
    sub.unsubscribe();

    expect(FakeEventSource.instances[0].closed).toBe(true);
  });
});
```

- [ ] **Step 8: Run it to verify it fails**

Run: `npm test`
Expected: FAIL, cannot resolve `./sse-client`.

- [ ] **Step 9: Write the SSE client**

`src/Admin.Web/src/app/core/host/sse-client.ts`:

```ts
import { Injectable, NgZone, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { OutputLine } from './host-types';

export type JobEvent = { kind: 'line'; line: OutputLine } | { kind: 'exited'; exitCode: number };

/**
 * One job's Server-Sent Events as an Observable. The browser's EventSource
 * reconnects on its own and resends Last-Event-ID, which the host honours;
 * `after` is for a deliberate reopen at a known sequence.
 */
@Injectable({ providedIn: 'root' })
export class SseClient {
  private readonly zone = inject(NgZone);

  follow(jobId: string, after = -1): Observable<JobEvent> {
    return new Observable<JobEvent>((subscriber) => {
      const source = new EventSource(`/api/jobs/${encodeURIComponent(jobId)}/stream?after=${after}`);

      source.addEventListener('line', (e: MessageEvent) =>
        this.zone.run(() => subscriber.next({ kind: 'line', line: JSON.parse(e.data) as OutputLine })),
      );
      source.addEventListener('exited', (e: MessageEvent) =>
        this.zone.run(() => {
          subscriber.next({ kind: 'exited', exitCode: (JSON.parse(e.data) as { exitCode: number }).exitCode });
          source.close();
          subscriber.complete();
        }),
      );

      return () => source.close();
    });
  }
}
```

- [ ] **Step 10: Run the specs**

Run: `npm test`
Expected: all pass.

- [ ] **Step 11: Write the shell and routes**

`src/Admin.Web/src/app/app.config.ts`:

```ts
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [provideBrowserGlobalErrorListeners(), provideRouter(routes), provideHttpClient(withFetch())],
};
```

`src/Admin.Web/src/app/app.routes.ts`:

```ts
import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'stack' },
  { path: 'stack', loadComponent: () => import('./features/stack/stack-page').then((m) => m.StackPage) },
  { path: 'logs', loadComponent: () => import('./features/logs/logs-page').then((m) => m.LogsPage) },
];
```

Placeholders, replaced in Tasks 10 and 11 (`src/Admin.Web/src/app/features/stack/stack-page.ts` and `.../logs/logs-page.ts`):

```ts
import { Component } from '@angular/core';

@Component({ selector: 'app-stack-page', template: '<h1>Stack</h1>' })
export class StackPage {}
```

```ts
import { Component } from '@angular/core';

@Component({ selector: 'app-logs-page', template: '<h1>Logs</h1>' })
export class LogsPage {}
```

`src/Admin.Web/src/app/app.ts`:

```ts
import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { HostClient } from './core/host/host-client';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly host = inject(HostClient);
  readonly config = toSignal(this.host.config());
}
```

`src/Admin.Web/src/app/app.html`:

```html
<header class="bar">
  <strong>blueprint-admin</strong>
  <nav>
    <a routerLink="/stack" routerLinkActive="active">Stack</a>
    <a routerLink="/logs" routerLinkActive="active">Logs</a>
  </nav>
  @if (config(); as c) {
    <span class="hint">{{ c.backendDir }} @if (c.fakePlatform) {<em>(fake platform)</em>}</span>
  }
</header>
<main><router-outlet /></main>
```

`src/Admin.Web/src/app/app.css`:

```css
.bar { display: flex; gap: 1.5rem; align-items: baseline; padding: 0.75rem 1rem; border-bottom: 1px solid #ddd; }
.bar nav a { margin-right: 1rem; text-decoration: none; }
.bar nav a.active { font-weight: 600; text-decoration: underline; }
.hint { margin-left: auto; font-size: 0.85rem; color: #666; }
main { padding: 1rem; }
```

Replace the generated `app.spec.ts` with:

```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  it('shows the backend directory from the host config', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    TestBed.inject(HttpTestingController).expectOne('/api/config').flush({
      backendDir: 'C:/dev/blueprint-backend', frontendDir: '', composeFile: '', fakePlatform: true,
      urls: { gateway: '', catalog: '', ordering: '', bff: '', keycloak: '', grafana: '', client: '' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('C:/dev/blueprint-backend');
    expect(fixture.nativeElement.textContent).toContain('(fake platform)');
  });
});
```

- [ ] **Step 12: Run lint, tests and a build**

Run: `npm run lint && npm test && npm run build`
Expected: all green; `../Admin.Host/wwwroot/index.html` exists afterwards.

- [ ] **Step 13: Run both dev servers and see the shell**

Terminal 1 from the repo root: `dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true`
Terminal 2 from `src/Admin.Web`: `npm start`
Open `http://127.0.0.1:5301/`. Expected: the header shows the backend directory and "(fake platform)", and the Stack and Logs links switch between the two placeholders.

- [ ] **Step 14: Commit**

```bash
git add src/Admin.Web
git commit -m "feat(web): Angular workspace, shell, host client and SSE client"
```

---

### Task 10: Output pane and the Stack screen

**Files:**
- Create: `src/Admin.Web/src/app/shared/output-pane/output-pane.ts`, `output-pane.spec.ts`
- Replace: `src/Admin.Web/src/app/features/stack/stack-page.ts`; create `stack-page.html`, `stack-page.css`, `stack-page.spec.ts`

**Interfaces:**
- Consumes: `HostClient`, `SseClient`, the wire types.
- Produces:
  - `OutputPane` component, selector `app-output-pane`, input `jobId: string | null`. Subscribes to `SseClient.follow(jobId)` when the id changes, renders each line in a `<pre>` with class `stderr` on stderr lines, shows `exited N` at the end, auto-scrolls. Unsubscribes on id change and destroy.
  - `StackPage`: loads `/api/stack` on init and every 3 seconds; a table of services (service, state, health, ports); a reachability strip; buttons **Up**, **Down**, **Down and wipe**; the wipe button opens an inline `<input>` where the user types `down -v` and the button stays disabled until the text matches; the started job's id goes to the output pane; links to client, Grafana, Keycloak from `/api/config`.

- [ ] **Step 1: Write the failing `OutputPane` spec**

`src/Admin.Web/src/app/shared/output-pane/output-pane.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { OutputPane } from './output-pane';

describe('OutputPane', () => {
  it('renders lines as they arrive and the exit code at the end', async () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();

    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stdout', text: 'one' } });
    events.next({ kind: 'line', line: { sequence: 1, at: '', stream: 'Stderr', text: 'two' } });
    events.next({ kind: 'exited', exitCode: 3 });
    fixture.detectChanges();

    const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;
    expect(sse.follow).toHaveBeenCalledWith('j1');
    expect(pre.textContent).toContain('one');
    expect(pre.querySelector('.stderr')?.textContent).toContain('two');
    expect(fixture.nativeElement.textContent).toContain('exited 3');
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm test`
Expected: FAIL, cannot resolve `./output-pane`.

- [ ] **Step 3: Write the pane**

`src/Admin.Web/src/app/shared/output-pane/output-pane.ts`:

```ts
import { Component, ElementRef, effect, inject, input, signal, viewChild, DestroyRef } from '@angular/core';
import { Subscription } from 'rxjs';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

/** Live output of one job. Give it a job id; it follows until the job exits. */
@Component({
  selector: 'app-output-pane',
  template: `
    <pre #pane class="pane">@for (l of lines(); track l.sequence) {<span [class.stderr]="l.stream === 'Stderr'">{{ l.text }}
</span>}@if (exitCode() !== null) {<span class="exit">exited {{ exitCode() }}</span>}</pre>
  `,
  styles: `
    .pane { max-height: 24rem; overflow: auto; background: #111; color: #ddd; padding: 0.75rem; font-size: 0.8rem; }
    .stderr { color: #f88; }
    .exit { color: #8cf; }
  `,
})
export class OutputPane {
  readonly jobId = input<string | null>(null);
  readonly lines = signal<OutputLine[]>([]);
  readonly exitCode = signal<number | null>(null);

  private readonly sse = inject(SseClient);
  private readonly pane = viewChild<ElementRef<HTMLElement>>('pane');
  private subscription?: Subscription;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const id = this.jobId();
      this.subscription?.unsubscribe();
      this.lines.set([]);
      this.exitCode.set(null);
      if (!id) {
        return;
      }
      this.subscription = this.sse.follow(id).subscribe((event) => {
        if (event.kind === 'line') {
          this.lines.update((all) => [...all, event.line]);
        } else {
          this.exitCode.set(event.exitCode);
        }
        queueMicrotask(() => {
          const el = this.pane()?.nativeElement;
          if (el) {
            el.scrollTop = el.scrollHeight;
          }
        });
      });
    });
  }
}
```

- [ ] **Step 4: Run the spec**

Run: `npm test`
Expected: `OutputPane` passes.

- [ ] **Step 5: Write the failing `StackPage` spec**

`src/Admin.Web/src/app/features/stack/stack-page.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { SseClient } from '../../core/host/sse-client';
import { StackPage } from './stack-page';

const stack = {
  backend: {
    reachable: true,
    error: null,
    services: [
      { service: 'gateway', state: 'running', health: 'healthy', exitCode: 0, publishedPorts: [5000] },
      { service: 'catalog-migrator', state: 'exited', health: null, exitCode: 0, publishedPorts: [] },
    ],
  },
  reachability: [{ name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 }],
};

const config = {
  backendDir: '', frontendDir: '', composeFile: '', fakePlatform: true,
  urls: { gateway: 'http://localhost:5000', catalog: '', ordering: '', bff: '', keycloak: 'http://localhost:8080', grafana: 'http://localhost:3000', client: 'http://localhost:5173' },
};

describe('StackPage', () => {
  let host: { stack: ReturnType<typeof vi.fn>; config: ReturnType<typeof vi.fn>; backendUp: ReturnType<typeof vi.fn>; backendDown: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    host = {
      stack: vi.fn(() => of(stack)),
      config: vi.fn(() => of(config)),
      backendUp: vi.fn(() => of({ id: 'up-1', commandLine: 'docker compose up', state: 'Running', exitCode: null, startedAt: '' })),
      backendDown: vi.fn(() => of({ id: 'down-1', commandLine: 'docker compose down', state: 'Running', exitCode: null, startedAt: '' })),
    };
    TestBed.configureTestingModule({
      imports: [StackPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  it('lists services with state, health and ports, and the reachability strip', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('tbody tr')) as HTMLElement[];
    expect(rows.map((r) => r.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'gateway running healthy 5000',
      'catalog-migrator exited — —',
    ]);
    expect(fixture.nativeElement.querySelector('.reachability')?.textContent).toContain('gateway');
  });

  it('starts an up job and hands its id to the output pane', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('button.up') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.backendUp).toHaveBeenCalled();
    expect(fixture.componentInstance.jobId()).toBe('up-1');
  });

  it('keeps the wipe button disabled until the confirmation is typed', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const wipe = fixture.nativeElement.querySelector('button.wipe') as HTMLButtonElement;
    expect(wipe.disabled).toBe(true);

    fixture.componentInstance.confirmText.set('down -v');
    fixture.detectChanges();
    expect(wipe.disabled).toBe(false);

    wipe.click();
    expect(host.backendDown).toHaveBeenCalledWith(true, 'down -v');
  });
});
```

- [ ] **Step 6: Run to verify it fails**

Run: `npm test`
Expected: FAIL on the placeholder: no `tbody tr`, no `button.up`.

- [ ] **Step 7: Write the Stack page**

`src/Admin.Web/src/app/features/stack/stack-page.ts`:

```ts
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { switchMap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputPane } from '../../shared/output-pane/output-pane';

@Component({
  selector: 'app-stack-page',
  imports: [FormsModule, OutputPane],
  templateUrl: './stack-page.html',
  styleUrl: './stack-page.css',
})
export class StackPage {
  private readonly host = inject(HostClient);

  /** Polled every 3 seconds; toSignal tears the subscription down with the component. */
  readonly stack = toSignal(timer(0, 3000).pipe(switchMap(() => this.host.stack())));
  readonly config = toSignal(this.host.config());
  readonly jobId = signal<string | null>(null);
  readonly confirmText = signal('');
  readonly canWipe = computed(() => this.confirmText() === 'down -v');
  readonly error = signal<string | null>(null);

  up(): void {
    this.host.backendUp().subscribe(this.started);
  }

  down(): void {
    this.host.backendDown(false).subscribe(this.started);
  }

  wipe(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendDown(true, this.confirmText()).subscribe(this.started);
    this.confirmText.set('');
  }

  private readonly started = {
    next: (job: { id: string }) => {
      this.error.set(null);
      this.jobId.set(job.id);
    },
    error: (e: { error?: { title?: string; detail?: string } }) =>
      this.error.set(e.error?.detail ?? e.error?.title ?? 'The host refused the request.'),
  };
}
```

The one-shot `subscribe` calls above complete on the first response, so they need no teardown.

`src/Admin.Web/src/app/features/stack/stack-page.html`:

```html
<section class="actions">
  <button class="up" (click)="up()">Up</button>
  <button class="down" (click)="down()">Down</button>
  <span class="wipe-box">
    <input placeholder="type: down -v" [ngModel]="confirmText()" (ngModelChange)="confirmText.set($event)" />
    <button class="wipe" [disabled]="!canWipe()" (click)="wipe()">Down and wipe</button>
  </span>
  @if (config(); as c) {
    <span class="links">
      <a [href]="c.urls.client" target="_blank">client</a>
      <a [href]="c.urls.grafana" target="_blank">grafana</a>
      <a [href]="c.urls.keycloak" target="_blank">keycloak</a>
    </span>
  }
</section>

@if (error(); as e) {
  <p class="error">{{ e }}</p>
}

@if (stack(); as s) {
  <p class="reachability">
    @for (r of s.reachability; track r.name) {
      <span [class.up]="r.up" [class.down]="!r.up" [title]="r.url">{{ r.name }} {{ r.status ?? '·' }}</span>
    }
  </p>

  @if (!s.backend.reachable) {
    <p class="error">Docker did not answer: {{ s.backend.error }}</p>
  }

  <table>
    <thead><tr><th>Service</th><th>State</th><th>Health</th><th>Ports</th></tr></thead>
    <tbody>
      @for (svc of s.backend.services; track svc.service) {
        <tr [class.exited]="svc.state === 'exited'">
          <td>{{ svc.service }}</td>
          <td>{{ svc.state }}</td>
          <td>{{ svc.health ?? '—' }}</td>
          <td>{{ svc.publishedPorts.length ? svc.publishedPorts.join(', ') : '—' }}</td>
        </tr>
      }
    </tbody>
  </table>
} @else {
  <p>Loading…</p>
}

<app-output-pane [jobId]="jobId()" />
```

`src/Admin.Web/src/app/features/stack/stack-page.css`:

```css
.actions { display: flex; gap: 0.75rem; align-items: center; margin-bottom: 1rem; flex-wrap: wrap; }
.links { margin-left: auto; display: flex; gap: 0.75rem; }
.reachability span { display: inline-block; margin-right: 0.75rem; padding: 0.1rem 0.4rem; border-radius: 3px; }
.reachability .up { background: #dfd; }
.reachability .down { background: #fdd; }
table { border-collapse: collapse; margin-bottom: 1rem; }
td, th { padding: 0.25rem 0.75rem; border-bottom: 1px solid #eee; text-align: left; }
tr.exited td { color: #888; }
.error { color: #b00; }
```

- [ ] **Step 8: Run lint and the specs**

Run: `npm run lint && npm test`
Expected: all green.

- [ ] **Step 9: See it against the fake host**

With the host in fake mode and `npm start` running, open `http://127.0.0.1:5301/stack`. Expected: thirteen rows, the two migrators greyed, seven green reachability chips; Up fills the output pane with six lines and `exited 0`; the wipe button enables only after typing `down -v`.

- [ ] **Step 10: Commit**

```bash
git add src/Admin.Web
git commit -m "feat(web): stack screen with up, down, typed wipe confirmation and live output"
```

---

### Task 11: The Logs screen

**Files:**
- Replace: `src/Admin.Web/src/app/features/logs/logs-page.ts`; create `logs-page.html`, `logs-page.css`, `logs-page.spec.ts`

**Interfaces:**
- Consumes: `HostClient.followLogs`, `SseClient.follow`, `OutputLine`.
- Produces: `LogsPage` with a service filter (checkboxes for the thirteen services, none checked = all), **Follow** and **Stop** buttons, a text filter applied client-side, a correlation-id box that highlights matching lines. Lines are kept in a signal capped at 5000.

- [ ] **Step 1: Write the failing spec**

`src/Admin.Web/src/app/features/logs/logs-page.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { LogsPage } from './logs-page';

describe('LogsPage', () => {
  let events: Subject<JobEvent>;
  let host: { followLogs: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    events = new Subject<JobEvent>();
    host = { followLogs: vi.fn(() => of({ id: 'logs-1', commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' })) };
    TestBed.configureTestingModule({
      imports: [LogsPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => events.asObservable() } },
      ],
    });
  });

  function line(sequence: number, text: string): JobEvent {
    return { kind: 'line', line: { sequence, at: '', stream: 'Stdout', text } };
  }

  it('follows the selected services and shows lines', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.selected.set(['gateway']);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));
    fixture.detectChanges();

    expect(host.followLogs).toHaveBeenCalledWith(['gateway']);
    expect(fixture.nativeElement.textContent).toContain('gateway | started');
  });

  it('filters by text and highlights the correlation id', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | proxying CorrelationId=abc'));
    events.next(line(1, 'catalog-api | listed products'));
    fixture.componentInstance.filter.set('gateway');
    fixture.componentInstance.correlationId.set('abc');
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.line')) as HTMLElement[];
    expect(rows.length).toBe(1);
    expect(rows[0].classList.contains('hit')).toBe(true);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm test`
Expected: FAIL, `selected` is not a property of the placeholder.

- [ ] **Step 3: Write the Logs page**

`src/Admin.Web/src/app/features/logs/logs-page.ts`:

```ts
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

export const COMPOSE_SERVICES = [
  'sql', 'redis-cache', 'redis-coordination', 'rabbitmq', 'keycloak', 'otel-collector', 'grafana',
  'catalog-migrator', 'catalog-api', 'gateway', 'ordering-migrator', 'ordering-api', 'web-bff',
];

const MAX_LINES = 5000;

@Component({
  selector: 'app-logs-page',
  imports: [FormsModule],
  templateUrl: './logs-page.html',
  styleUrl: './logs-page.css',
})
export class LogsPage {
  private readonly host = inject(HostClient);
  private readonly sse = inject(SseClient);
  private subscription?: Subscription;

  readonly services = COMPOSE_SERVICES;
  readonly selected = signal<string[]>([]);
  readonly following = signal(false);
  readonly filter = signal('');
  readonly correlationId = signal('');
  readonly lines = signal<OutputLine[]>([]);

  readonly visible = computed(() => {
    const needle = this.filter().toLowerCase();
    return needle ? this.lines().filter((l) => l.text.toLowerCase().includes(needle)) : this.lines();
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  toggle(service: string, on: boolean): void {
    this.selected.update((all) => (on ? [...all, service] : all.filter((s) => s !== service)));
  }

  follow(): void {
    this.stop();
    this.lines.set([]);
    this.host.followLogs(this.selected()).subscribe((job) => {
      this.following.set(true);
      this.subscription = this.sse.follow(job.id).subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => (all.length >= MAX_LINES ? [...all.slice(1), event.line] : [...all, event.line]));
          } else {
            this.following.set(false);
          }
        },
        complete: () => this.following.set(false),
      });
    });
  }

  stop(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.following.set(false);
  }

  clear(): void {
    this.lines.set([]);
  }

  isHit(line: OutputLine): boolean {
    const id = this.correlationId();
    return id.length > 0 && line.text.includes(id);
  }
}
```

Stopping the SSE subscription closes the browser's EventSource; the host's `docker compose logs -f` job keeps running until the host exits. Phase 2 adds a `POST /api/jobs/{id}/stop` when the frontend supervisor needs it, and the Logs page will call it then.

`src/Admin.Web/src/app/features/logs/logs-page.html`:

```html
<section class="controls">
  <div class="services">
    @for (s of services; track s) {
      <label><input type="checkbox" [checked]="selected().includes(s)" (change)="toggle(s, $any($event.target).checked)" /> {{ s }}</label>
    }
  </div>
  <div class="buttons">
    <button class="follow" (click)="follow()">Follow</button>
    <button class="stop" [disabled]="!following()" (click)="stop()">Stop</button>
    <button (click)="clear()">Clear</button>
    <input placeholder="filter text" [ngModel]="filter()" (ngModelChange)="filter.set($event)" />
    <input placeholder="correlation id" [ngModel]="correlationId()" (ngModelChange)="correlationId.set($event)" />
    @if (following()) {<span class="live">live</span>}
  </div>
</section>

<pre class="log">@for (l of visible(); track l.sequence) {<span class="line" [class.hit]="isHit(l)" [class.stderr]="l.stream === 'Stderr'">{{ l.text }}
</span>}</pre>
```

`src/Admin.Web/src/app/features/logs/logs-page.css`:

```css
.controls { display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 0.75rem; }
.services { display: flex; flex-wrap: wrap; gap: 0.75rem; font-size: 0.85rem; }
.buttons { display: flex; gap: 0.5rem; align-items: center; }
.live { color: #080; font-weight: 600; }
.log { height: 60vh; overflow: auto; background: #111; color: #ddd; padding: 0.75rem; font-size: 0.8rem; }
.line.hit { background: #443; }
.line.stderr { color: #f88; }
```

- [ ] **Step 4: Run lint and the specs**

Run: `npm run lint && npm test`
Expected: all green.

- [ ] **Step 5: See it against the fake host**

Open `http://127.0.0.1:5301/logs`, press Follow, type `fake-corr-0001` in the correlation box. Expected: twelve lines arrive, two are highlighted, `live` stays on, Stop clears it.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Web
git commit -m "feat(web): logs screen with service filter, text filter and correlation highlight"
```

---

### Task 12: Serve the SPA from the host and the Playwright smoke

**Files:**
- Modify: `src/Admin.Host/Program.cs` (static files and the SPA fallback)
- Create: `src/Admin.Web/playwright.config.ts`, `src/Admin.Web/e2e/stack.spec.ts`, `src/Admin.Web/e2e/logs.spec.ts`
- Modify: `src/Admin.Web/tsconfig.json` (exclude `e2e` from the app build), `src/Admin.Web/eslint.config.js` (lint `e2e` too)

**Interfaces:**
- Consumes: everything above.
- Produces: `GET /` and any non-`/api` path serve `wwwroot/index.html` when a build exists; `/api/*` is unaffected. `npm run e2e` builds nothing itself: it expects `npm run build` to have run, starts the host in FakePlatform mode on 5300, and drives the console at `http://127.0.0.1:5300`.

- [ ] **Step 1: Serve the built SPA**

In `src/Admin.Host/Program.cs`, after `app.UseStatusCodePages();` add:

```csharp
// The SPA build lands in wwwroot (angular.json's outputPath). In development
// the Angular dev server serves it instead and proxies /api here; when there
// is no wwwroot the fallback answers 404 and the API is unaffected.
app.UseDefaultFiles();
app.UseStaticFiles();
```

and after `app.MapStack();` add:

```csharp
app.MapFallbackToFile("index.html").ShortCircuit();
```

`ShortCircuit()` keeps the fallback from running the middleware pipeline twice; remove it if the SDK in use rejects the call on a fallback endpoint, since the behaviour without it is the same for this host.

Confirm the API is unaffected: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test` still all green (the `AdminHostFactory` has no wwwroot, and no test asks for `/`).

- [ ] **Step 2: Write the Playwright config**

`src/Admin.Web/playwright.config.ts`:

```ts
import { defineConfig, devices } from '@playwright/test';

const url = 'http://127.0.0.1:5300';

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL: url, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    // The host serves the SPA from wwwroot, so `npm run build` must have run first.
    command: 'dotnet run --project ../Admin.Host --no-build -- --Admin:FakePlatform=true',
    cwd: __dirname,
    url: `${url}/api/config`,
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
});
```

`--no-build` means `dotnet build` must have run before `npm run e2e`; the CI job does both, and the README says so.

In `tsconfig.json` add `"exclude": ["e2e/**"]` at the top level if the generated file has none, so `ng build` ignores the Playwright specs. In `eslint.config.js` the generated TypeScript block's `files` pattern is `**/*.ts`, which already covers `e2e/`; no change unless lint reports the Playwright `test` global, in which case add `languageOptions: { globals: { test: 'readonly', expect: 'readonly' } }` to that block.

- [ ] **Step 3: Write the smoke specs**

`src/Admin.Web/e2e/stack.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

test('the stack screen lists the fake services and runs up', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('tbody tr')).toHaveCount(13);
  await expect(page.locator('tbody tr', { hasText: 'gateway' })).toContainText('healthy');
  await expect(page.locator('.reachability span.up')).toHaveCount(7);

  await page.getByRole('button', { name: 'Up' }).click();
  await expect(page.locator('app-output-pane pre')).toContainText('commerce-gateway-1  Healthy');
  await expect(page.locator('app-output-pane pre')).toContainText('exited 0');
});

test('wiping volumes needs the typed confirmation', async ({ page }) => {
  await page.goto('/stack');
  const wipe = page.getByRole('button', { name: 'Down and wipe' });

  await expect(wipe).toBeDisabled();
  await page.getByPlaceholder('type: down -v').fill('down -v');
  await expect(wipe).toBeEnabled();

  await wipe.click();
  await expect(page.locator('app-output-pane pre')).toContainText('Volume commerce_sql-data  Removed');
});
```

`src/Admin.Web/e2e/logs.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

test('following logs streams lines and highlights a correlation id', async ({ page }) => {
  await page.goto('/logs');

  await page.getByLabel('gateway').check();
  await page.getByRole('button', { name: 'Follow' }).click();

  await expect(page.locator('.log .line')).toHaveCount(12);
  await expect(page.locator('.live')).toBeVisible();

  await page.getByPlaceholder('correlation id').fill('fake-corr-0001');
  await expect(page.locator('.log .line.hit')).toHaveCount(2);

  await page.getByPlaceholder('filter text').fill('ordering-api');
  await expect(page.locator('.log .line')).toHaveCount(2);

  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.live')).toHaveCount(0);
});
```

- [ ] **Step 4: Run the smoke**

From the repo root then `src/Admin.Web`:

```bash
dotnet format whitespace BlueprintAdmin.slnx && dotnet build
cd src/Admin.Web
npm run build
npx playwright install chromium
npm run e2e
```

Expected: 3 passed. If the host is already running from an earlier step, Playwright reuses it outside CI; stop it first if it is not in fake mode.

- [ ] **Step 5: Commit**

```bash
git add src/Admin.Host/Program.cs src/Admin.Web/playwright.config.ts src/Admin.Web/e2e src/Admin.Web/tsconfig.json src/Admin.Web/eslint.config.js
git commit -m "feat: serve the SPA from the host and add the Playwright smoke"
```

---

### Task 13: CI

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: three jobs. `host` on ubuntu and windows: build and test. `web` on ubuntu: lint, unit tests, build. `smoke` on ubuntu: build both, Playwright against the FakePlatform host. No job needs Docker or the sibling clones.

- [ ] **Step 1: Write the workflow**

`.github/workflows/ci.yml`:

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  host:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      # ProcessRunnerTests spawn node; it is the one long-running command that
      # behaves the same on both runners.
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
      - run: dotnet build --configuration Release
      - run: dotnet test --configuration Release --no-build

  web:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/Admin.Web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
          cache: npm
          cache-dependency-path: src/Admin.Web/package-lock.json
      - run: npm ci
      - run: npm run lint
      - run: npm test
      - run: npm run build

  smoke:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
          cache: npm
          cache-dependency-path: src/Admin.Web/package-lock.json
      - run: dotnet build
      - run: npm ci
        working-directory: src/Admin.Web
      - run: npm run build
        working-directory: src/Admin.Web
      - run: npx playwright install --with-deps chromium
        working-directory: src/Admin.Web
      - run: npm run e2e
        working-directory: src/Admin.Web
      - if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: playwright-report
          path: src/Admin.Web/playwright-report/
```

- [ ] **Step 2: Reproduce the three jobs locally**

From the repo root:

```bash
dotnet build --configuration Release && dotnet test --configuration Release --no-build
cd src/Admin.Web && npm ci && npm run lint && npm test && npm run build && cd ../..
dotnet build && cd src/Admin.Web && npm run e2e
```

Expected: every command exits 0.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "chore(ci): build, test, lint and smoke on push and pull request"
```

---

### Task 14: README and CLAUDE.md

**Files:**
- Create: `README.md`, `CLAUDE.md`

**Interfaces:**
- Produces: the two documents a newcomer and Claude read first. Both cite owners rather than restating facts: ports and paths point at `appsettings.json` and `angular.json`, screens at the spec.

- [ ] **Step 1: Write `README.md`**

```markdown
# blueprint-admin

A local admin console for [`dotnet-ddd-blueprint`](https://github.com/alexander-shamray/dotnet-ddd-blueprint):
one page that runs, stops, observes and exercises the platform, doing verbatim
what the workspace's `run-locally.md` says a developer does by hand.

It runs on the developer's workstation, binds loopback only, and holds the
realm passwords, so it is never deployed and never shared. The design is
`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`.

## Prerequisites

Docker Desktop, the .NET SDK pinned in `global.json`, the Node version in
`.nvmrc`, and the two sibling clones beside this one:

    ../blueprint-backend    alexander-shamray/dotnet-ddd-blueprint
    ../blueprint-frontend   alexander-shamray/blueprint-frontend

Other locations: set `Admin:BackendDir` and `Admin:FrontendDir` (see
`src/Admin.Host/appsettings.json`) on the command line
(`--Admin:BackendDir=D:/work/backend`) or as `BLUEPRINT_Admin__BackendDir`.
The host refuses to start if either is wrong, naming the key.

## Run it

From this directory:

    dotnet run --project src/Admin.Host

Open **http://127.0.0.1:5300**. The console is the built SPA under
`src/Admin.Host/wwwroot`; build it once with `npm ci && npm run build` in
`src/Admin.Web`.

To work on the SPA, run the host as above and, in `src/Admin.Web`, `npm start`;
open **http://127.0.0.1:5301**, which proxies `/api` to the host.

## No platform? Fake it

    dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true

Every process and every HTTP surface is replayed from recordings in
`src/Admin.Host/Fakes/`. This is how the SPA is developed and how CI runs the
Playwright smoke; it needs no Docker and no clones.

## What it does today

Phase 0 and 1 of the spec: the Stack screen (Compose services, reachability,
up, down, down with a typed `down -v` confirmation, live output) and the Logs
screen (follow, service filter, text filter, correlation-id highlight).
Frontend start/stop, the API console, broker inspection and the event trace
are the spec's later phases.

## Tests

    dotnet test                                # host, xunit
    cd src/Admin.Web && npm test               # SPA, Vitest
    dotnet build && cd src/Admin.Web && npm run build && npm run e2e   # Playwright against the fake host

`.github/workflows/ci.yml` runs the same three on every push and pull request.
```

- [ ] **Step 2: Write `CLAUDE.md`**

```markdown
# CLAUDE.md

Guidance for Claude Code in this repository: what it is, where things are,
how to act. Facts that move with a PR live elsewhere and are cited from here.

## What this repo is

`blueprint-admin` is the local admin console for `dotnet-ddd-blueprint`. The
specification is `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`;
read its §1 before changing behaviour and its §13 before adding any. The
implementation plans are under `docs/superpowers/plans/`.

Two projects, mirroring the sibling repositories:

    src/Admin.Host/      .NET 10 minimal API. Runs docker compose and npm as child
                         processes, proxies HTTP to the platform, streams output over SSE.
    src/Admin.Web/       Angular 22 standalone SPA, the console. Built into Admin.Host/wwwroot.
    tests/Admin.Host.Tests/  xunit. The SPA's Vitest specs sit beside the code; Playwright in src/Admin.Web/e2e.

Owners of facts you will be tempted to restate: ports and URLs are
`src/Admin.Host/appsettings.json` and `src/Admin.Web/angular.json`; the
Compose service list is the backend's `deploy/compose/`; the fake platform's
recordings are `src/Admin.Host/Fakes/`.

## Rules that the build enforces

- .NET SDK is pinned by `global.json` with roll-forward disabled.
- `TreatWarningsAsErrors`; IDE0055, IDE0065 and IDE0161 are build errors.
  No column alignment of `=` or `=>`.
- Every `.cs` is CRLF (`.gitattributes`). Files written with LF fail IDE0055
  on every line: run `dotnet format whitespace BlueprintAdmin.slnx` before
  building anything you created.
- Test names are sentences with underscores.

## How to act here

- The host binds loopback only and has no login. Never add a listener, a CORS
  header or a credential store; spec §8 says why.
- Every command the host runs is a line from the workspace's `run-locally.md`.
  If the console needs something that document does not do, change the
  document's owner first, not the console.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited from
  here. If a backend fact is not observable, raise it there.
- FakePlatform is a first-class mode: a new endpoint is not done until it
  works against the fakes and the Playwright smoke covers its screen.
- TDD, one test cycle per change, small commits with `feat:`/`fix:`/`test:`/
  `chore:`/`docs:` prefixes.

## Running and testing

See `README.md`. Short form: `dotnet run --project src/Admin.Host`
(`-- --Admin:FakePlatform=true` for no platform), `npm start` in
`src/Admin.Web` for the dev server on 5301, `dotnet test`, `npm test`,
`npm run e2e`.
```

- [ ] **Step 3: Check every path the two files name exists**

Run from the repo root:

```bash
ls docs/superpowers/specs/2026-09-14-blueprint-admin-design.md src/Admin.Host/appsettings.json src/Admin.Web/angular.json src/Admin.Host/Fakes .github/workflows/ci.yml global.json .nvmrc .gitattributes
```

Expected: every path listed, no error.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: README and CLAUDE.md for phases 0 and 1"
```

---

## Self-review against the spec

- §4 repository shape: Tasks 1, 2, 9, 12, 13, 14. `docs/admin-architecture.md` is deferred by the spec itself ("written once the code exists").
- §5.1 Config: Task 2. §5.2 ProcessRunner and Jobs: Tasks 3, 4, 5. §5.3 Compose: Task 6. §5.10 endpoints for `/stack`, `/jobs`, `/logs/follow`: Tasks 5, 7. `/stack/frontend/*`, `/broker/*`, `/identity/*`, `/catalog/*`, `/proxy`, `/trace/*`, `/telemetry/health` are phases 2 to 5.
- §6 Stack and Logs screens: Tasks 10, 11. The Stack screen's frontend job row is phase 2.
- §8 loopback: Task 2 (`Listen(IPAddress.Loopback, port)`, no CORS registration anywhere). `down -v` confirmation: Task 7 host-side, Task 10 client-side.
- §9 error handling: `ComposeStatus.Unreachable` (Task 6), exit code and tail on the job (Tasks 3, 5), problem details (Task 2's `AddProblemDetails`, Task 5's 404, Task 7's 400).
- §10 testing: fake runner and fixtures (Tasks 4, 8), `WebApplicationFactory` with SSE resume (Task 5), Vitest per screen (Tasks 9 to 11), Playwright against FakePlatform (Task 12). The Docker-gated integration class for `ps` against the real CLI is not in this plan: it needs a runner with Docker and the spec allows it to be skipped; it is added in the phase 2 plan alongside the frontend supervisor's real-process test.
- §11 CI: Task 13, two runners for the host.
- Naming used across tasks: `IProcessRunner.Start/StopAsync`, `Job.Append/MarkExited/Since/Tail/Follow/Completion`, `JobRegistry.Create/Find/All`, `ComposeService.Up/Down/FollowLogs/Exec/PsAsync`, `PlatformProbe.ProbeAsync`, `FakePlatformScripts.Script/ComposePsLines`, `HostClient.config/stack/backendUp/backendDown/followLogs/job`, `SseClient.follow` — each defined once and used with the same signature everywhere above.
