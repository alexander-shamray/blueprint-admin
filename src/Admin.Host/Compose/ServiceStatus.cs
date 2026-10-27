namespace Admin.Host.Compose;

/// <summary>
/// One Compose service as <c>docker compose ps</c> reports it. Health is null when the service declares no healthcheck.
/// <see cref="ConfigFiles"/> are the names of the Compose files its container was created from, as Compose labels it, so an
/// override in force is observed rather than remembered; null when <c>ps</c> printed no labels.
/// </summary>
public sealed record ServiceStatus(string Service, string State, string? Health, int? ExitCode, IReadOnlyList<int> PublishedPorts, IReadOnlyList<string>? ConfigFiles = null);
