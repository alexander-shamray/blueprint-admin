namespace Admin.Host.Stack;

/// <summary>
/// Restores, at host start, the lease a previous host died holding (spec §5.3). It runs in the background so a slow
/// restore does not hold the host from listening, and a failure is logged and left on the lease for the Stack screen.
/// </summary>
public sealed partial class LeaseReconciler(ScenarioLeaseService leases, ILogger<LeaseReconciler> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await leases.ReconcileAsync();
            }
            catch (Exception ex)
            {
                ReconcileFailed(logger, ex);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "The lease journal could not be reconciled at start.")]
    private static partial void ReconcileFailed(ILogger logger, Exception exception);
}
