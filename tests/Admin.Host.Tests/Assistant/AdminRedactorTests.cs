using Admin.Assistant;
using Admin.Host.Assistant;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>
/// The admin's redactor and its one exemption (AI assistant design §10, #465): a realm user still on the password the
/// realm export publishes for it is not a secret, so its name survives; any other password is redacted as given.
/// </summary>
public sealed class AdminRedactorTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>, IDisposable
{
    private readonly List<WebApplicationFactory<Program>> hosts = [];

    public void Dispose()
    {
        foreach (WebApplicationFactory<Program> host in hosts)
        {
            host.Dispose();
        }
    }

    private IRedactor Redactor(params (string Key, string Value)[] settings)
    {
        WebApplicationFactory<Program> host = factory.WithWebHostBuilder(b =>
        {
            foreach ((string key, string value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        hosts.Add(host);

        return host.Services.GetRequiredService<AssistantRuntime>().Core!.Redactor;
    }

    [Fact]
    public void The_default_users_published_passwords_leave_their_names_and_the_privacy_service_alone()
    {
        const string Text = "demo signs in as browser; privacy-api drains privacy-completions for the privacy user";

        Redactor().Redact(Text, AssistantRuntime.Operator).ShouldBe(Text);
    }

    [Fact]
    public void A_default_username_configured_with_its_published_password_is_still_exempt()
    {
        Redactor(("Admin:Users:0:Username", "demo"), ("Admin:Users:0:Password", "demo"))
            .Redact("demo", AssistantRuntime.Operator)
            .ShouldBe("demo");
    }

    [Fact]
    public void A_password_changed_from_its_default_is_redacted()
    {
        Redactor(("Admin:Users:0:Username", "demo"), ("Admin:Users:0:Password", "demo-changed-pa55"))
            .Redact("signed in with demo-changed-pa55", AssistantRuntime.Operator)
            .ShouldBe($"signed in with {FixtureScrubber.Scrubbed}");
    }

    [Fact]
    public void Another_users_password_that_matches_a_default_name_is_redacted()
    {
        // Only the published pair is exempt: "privacy" as the password of a user the realm export does not ship is a
        // secret like any other, so the word goes.
        Redactor(("Admin:Users:0:Username", "ops"), ("Admin:Users:0:Password", "privacy"))
            .Redact("privacy-api", AssistantRuntime.Operator)
            .ShouldBe($"{FixtureScrubber.Scrubbed}-api");
    }
}
