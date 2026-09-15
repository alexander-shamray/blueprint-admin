using System.Text.Json.Serialization;
using Admin.Host.Identity;

namespace Admin.Host.Api;

/// <summary>One request for <c>POST /api/proxy</c> (spec §5.7). No identity, or one with no username, is anonymous.</summary>
public sealed record ProxyRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Headers,
    string? Body,
    IdentityRequest? Identity,
    string? CorrelationId);

/// <summary>
/// What became of a proxied request. <c>responded</c> carries the platform's answer untouched;
/// the other two are the "never reached the upstream" shape spec §9 asks the SPA to render differently.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(ProxyResponded), "responded")]
[JsonDerivedType(typeof(ProxyUnreached), "unreached")]
[JsonDerivedType(typeof(ProxyTokenRejected), "tokenRejected")]
public abstract record ProxyResult(string CorrelationId);

public sealed record ProxyResponded(int Status, IReadOnlyDictionary<string, string[]> Headers, string Body, bool BodyTruncated, long ElapsedMs, string CorrelationId)
    : ProxyResult(CorrelationId);

public sealed record ProxyUnreached(string Error, long ElapsedMs, string CorrelationId) : ProxyResult(CorrelationId);

/// <summary>Keycloak refused the identity; its status and body, and the request was not sent.</summary>
public sealed record ProxyTokenRejected(int Status, string Body, string CorrelationId) : ProxyResult(CorrelationId);
