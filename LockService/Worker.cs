namespace LockService;

public sealed class Worker(LockManager manager, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await manager.ExpireAsync(stoppingToken); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(exception, "Unable to persist expiration; will retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
