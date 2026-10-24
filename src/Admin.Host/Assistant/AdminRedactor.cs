using Admin.Assistant;
using Admin.Host.Broker;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Identity;

namespace Admin.Host.Assistant;

/// <summary>
/// The admin's redactor (AI assistant design §10): <see cref="FixtureScrubber"/>'s rules first, then every secret the
/// host knows, as exact values, longest first: each realm user's password, <see cref="AdminOptions.SqlPassword"/>, the
/// password in <see cref="AdminOptions.BffBrokerConnection"/>, and the dead-letter operator's password while a run holds
/// one, each also in the quoted form the host writes it into a command line. Exact values fail closed: the local realm's default users have their names for passwords, so those names are
/// redacted too, wherever they appear.
/// </summary>
public sealed class AdminRedactor(AdminOptions options, DeadLetterService deadLetters) : IRedactor
{
    public string Redact(string text, Caller caller)
    {
        string scrubbed = FixtureScrubber.Scrub(text);

        foreach (string secret in Secrets().OrderByDescending(s => s.Length))
        {
            scrubbed = scrubbed.Replace(secret, FixtureScrubber.Scrubbed, StringComparison.Ordinal);
        }

        return scrubbed;
    }

    private IEnumerable<string> Secrets()
    {
        List<string> secrets = [.. RealmUsers.Of(options).Select(u => u.Password), options.SqlPassword];

        if (Uri.TryCreate(options.BffBrokerConnection, UriKind.Absolute, out Uri? broker) && broker.UserInfo.IndexOf(':', StringComparison.Ordinal) is int colon and >= 0)
        {
            string password = broker.UserInfo[(colon + 1)..];
            secrets.Add(password);
            secrets.Add(Uri.UnescapeDataString(password));
        }

        if (deadLetters.MintedPassword is { } minted)
        {
            secrets.Add(minted);
        }

        // Each also as the host writes it into a PowerShell single-quoted string (LoadRunService's blocks), which a
        // command line in a read carries: a quote in the secret is doubled there, so the exact value would not match.
        return secrets
            .SelectMany(s => new[] { s, s.Replace("'", "''", StringComparison.Ordinal) })
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal);
    }
}
