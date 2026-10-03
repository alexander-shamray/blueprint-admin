namespace Admin.Host.Api;

public sealed record ApiParameter(string Name, bool Required, string? Type);

/// <summary>
/// One call the API screen can make (spec §5.7). <see cref="Url"/> is absolute and may hold
/// <c>{name}</c> placeholders for <see cref="PathParameters"/>; <see cref="ExampleBody"/> is JSON text.
/// </summary>
public sealed record ApiOperation(
    string Id,
    string Source,
    string Name,
    string Method,
    string Url,
    IReadOnlyList<ApiParameter> PathParameters,
    IReadOnlyList<ApiParameter> QueryParameters,
    string? ExampleBody,
    bool HasCommandId,
    string EdgePolicy,
    bool Available);

/// <param name="Changes">The document against its kept baseline (spec §5.7); null when no document was read this time.</param>
public sealed record ApiSource(string Name, string DocumentUrl, bool Available, string? Error, OpenApiChanges? Changes = null);

public sealed record ApiCatalogView(IReadOnlyList<ApiSource> Sources, IReadOnlyList<ApiOperation> Operations);
