using Microsoft.Extensions.Options;
using NotificationApi.Configuration;

namespace NotificationApi.Services;

// Calls ProcessPendingAsync every few seconds so nobody has to call /process by hand.
// Only registered when Notifications:WorkerEnabled is true.
public class NotificationProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationProcessingWorker> _logger;

    public NotificationProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<NotificationOptions> options,
        ILogger<NotificationProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification worker started, running every {Seconds}s", _options.WorkerIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.WorkerIntervalSeconds));

        do
        {
            try
            {
                // The service and DbContext are scoped, so every run gets its own scope.
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<INotificationService>();

                var processed = await service.ProcessPendingAsync(stoppingToken);
                if (processed.Count > 0)
                    _logger.LogInformation("Worker processed {Count} notification(s)", processed.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database hiccup must not kill the worker; try again on the next tick.
                _logger.LogError(ex, "Worker run failed");
            }
        }
        while (await WaitNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
