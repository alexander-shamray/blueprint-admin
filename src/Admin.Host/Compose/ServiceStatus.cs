namespace Admin.Host.Compose;

/// <summary>One Compose service as <c>docker compose ps</c> reports it. Health is null when the service declares no healthcheck.</summary>
public sealed record ServiceStatus(string Service, string State, string? Health, int? ExitCode, IReadOnlyList<int> PublishedPorts);
