using NotificationApi.Models;

namespace NotificationApi.Providers;

/// <summary>The external system that really delivers the message (SMTP, SendGrid, ...).</summary>
public interface INotificationProvider
{
    /// <summary>Sends the notification. Throws if the provider could not deliver it.</summary>
    Task SendAsync(Notification notification, CancellationToken ct = default);
}
