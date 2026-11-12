namespace Doctheca.Host.Authentication;

/// <summary>
/// Periodically reclaims expired tickets of the in-process <see cref="AdminOidcTicketStore"/> so
/// sessions abandoned by their browser never hold capacity, mirroring the replaced package's
/// <c>TicketStoreCleanupService</c> under the same five-minute cadence.
/// </summary>
public sealed class AdminOidcTicketStoreCleanupService(
    AdminOidcTicketStore store,
    ILogger<AdminOidcTicketStoreCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AdminOidcTicketStore.CleanupInterval);
        do
        {
            try
            {
                var removed = store.RemoveExpired();
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} expired admin session tickets", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep never stops the service; the next tick retries it.
                logger.LogWarning(exception, "Admin session ticket sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
