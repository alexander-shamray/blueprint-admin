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

/// <summary>
/// The upstream answered. <c>BodyTruncated</c> means the 1 MiB cap cut the body; <c>BodyError</c>, null when the body
/// was read to its end, says why the read broke off after the headers had arrived.
/// </summary>
public sealed record ProxyResponded(int Status, IReadOnlyDictionary<string, string[]> Headers, string Body, bool BodyTruncated, string? BodyError, long ElapsedMs, string CorrelationId)
    : ProxyResult(CorrelationId);

public sealed record ProxyUnreached(string Error, long ElapsedMs, string CorrelationId) : ProxyResult(CorrelationId);

/// <summary>Keycloak refused the identity; its status and body, and the request was not sent.</summary>
public sealed record ProxyTokenRejected(int Status, string Body, string CorrelationId) : ProxyResult(CorrelationId);
