namespace Admin.Host.Fakes;

/// <summary>
/// What the BFF projection rebuild's commands printed on this workstation on 2026-10-15, with the stack up and no order
/// placed (so Catalog's product events were the only ones in any outbox window), for <see cref="FakePlatformScripts"/>
/// to replay. Recorded by running run-locally.md's lines by hand; kept here for the reason
/// <see cref="WorkstationRecordings"/> is.
/// </summary>
internal static class ReplayRecordings
{
    /// <summary><c>dotnet run --project tools/bff-replay</c>: every row in the window was already in the BFF's inbox.</summary>
    public const string Repair = """
        Catalog: no event of ADR-051's eight sent from its outbox window.
        Ordering: no event of ADR-051's eight sent from its outbox window.
        Payments: no event of ADR-051's eight sent from its outbox window.
        Shipping: no event of ADR-051's eight sent from its outbox window.
        Skipped 24 event(s) the BFF's inbox had handled.
        Sent 0 event(s) to bff-order-events.
        """;

    /// <summary><c>dotnet run --project tools/bff-replay -- --reset</c>, with the BFF stopped: the inbox was emptied, so all 24 are sent.</summary>
    public const string Rebuild = """
        Reset: the order rows and bff-order-events's inbox rows are deleted.
        Catalog: 24 event(s) sent, the oldest from 2026-10-15T05:46:30.8941429+00:00.
        Ordering: no event of ADR-051's eight sent from its outbox window.
        Payments: no event of ADR-051's eight sent from its outbox window.
        Shipping: no event of ADR-051's eight sent from its outbox window.
          Common.Contracts.Catalog.V1.ProductPublished: 24
        Sent 24 event(s) to bff-order-events.
        """;

    /// <summary><c>docker compose … stop web-bff</c>.</summary>
    public const string StopBff = """
         Container commerce-web-bff-1 Stopping
         Container commerce-web-bff-1 Stopped
        """;

    /// <summary><c>docker compose … start web-bff</c>.</summary>
    public const string StartBff = """
         Container commerce-object-store-init-1 Exited
         Container commerce-keycloak-1 Healthy
         Container commerce-bff-migrator-1 Exited
         Container commerce-web-bff-1 Starting
         Container commerce-web-bff-1 Started
        """;
}
