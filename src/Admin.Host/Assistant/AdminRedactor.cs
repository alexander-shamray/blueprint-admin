using System.Text.Json;
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
/// one. Each is also matched in the forms the host writes it in: quoted into a command line, and escaped inside a read
/// tool's JSON. Exact values fail closed, with one exemption (#465): a realm user whose password is still the one the
/// realm export publishes for it (<see cref="RealmUsers.HasPublishedPassword"/>). Those defaults are each user's own
/// name, so redacting them would take <c>demo</c>, <c>browser</c> and <c>privacy</c> out of every question and read,
/// the Privacy service's name among them, and protect nothing, since they are in the backend's public repository. A
/// password changed from its default is redacted as any other.
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
        List<string> secrets = [.. RealmUsers.Of(options).Where(u => !RealmUsers.HasPublishedPassword(u)).Select(u => u.Password), options.SqlPassword];

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

        // Each also as the host writes it elsewhere, where the exact value would not match: in a PowerShell single-quoted
        // string, a quote doubled (a load run step's command line, LoadRunService), and inside a read tool's JSON
        // result, escaped by the encoder AdminReadTools serializes with.
        return secrets
            .SelectMany(s => new[] { s, s.Replace("'", "''", StringComparison.Ordinal) })
            .SelectMany(s => new[] { s, JsonEncodedText.Encode(s, AdminReadTools.Json.Encoder).Value })
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal);
    }
}
