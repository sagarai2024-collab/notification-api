using NotificationApi.Models;

namespace NotificationApi.Providers;

// Pretends to send an email by writing a log line.
// To try the failure path from Swagger, use a recipient that starts with "fail", e.g. fail@example.com.
public class SimulatedEmailProvider : INotificationProvider
{
    private readonly ILogger<SimulatedEmailProvider> _logger;

    public SimulatedEmailProvider(ILogger<SimulatedEmailProvider> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        if (notification.Recipient.StartsWith("fail", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Simulated provider failure.");

        _logger.LogInformation(
            "Email sent to {Recipient} (notification {Id}, priority {Priority}): {Subject}",
            notification.Recipient, notification.Id, notification.Priority, notification.Subject);

        return Task.CompletedTask;
    }
}
