using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationApi.Configuration;
using NotificationApi.Dtos;
using NotificationApi.Models;
using NotificationApi.Providers;
using NotificationApi.Repositories;
using NotificationApi.Services;
using NotificationApi.Validation;

namespace NotificationApi.Tests;

// A clock we can move by hand, so time-based rules are tested without Thread.Sleep.
public class FakeClock : TimeProvider
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

// Remembers the order of sends and can be told to fail for some recipients.
public class FakeProvider : INotificationProvider
{
    public List<Notification> Sent { get; } = new();
    public HashSet<string> FailFor { get; } = new();
    public int Calls { get; private set; }

    public Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        Calls++;
        if (FailFor.Contains(notification.Recipient))
            throw new InvalidOperationException("provider down");

        Sent.Add(notification);
        return Task.CompletedTask;
    }
}

public class ServiceFixture
{
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public FakeClock Clock { get; } = new(Start);
    public FakeProvider Provider { get; } = new();
    public InMemoryNotificationRepository Repository { get; } = new();
    public NotificationOptions Options { get; }
    public NotificationService Service { get; }

    public ServiceFixture(NotificationOptions? options = null)
    {
        Options = options ?? new NotificationOptions();   // defaults: 3 attempts, 30s base delay, 5 min windows
        Service = new NotificationService(
            Repository, Provider, new NotificationRequestValidator(), Clock,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<NotificationService>.Instance);
    }

    public static CreateNotificationRequest ValidRequest(
        string recipient = "customer@example.com",
        string priority = "Normal",
        string message = "Your order has been confirmed.",
        DateTimeOffset? deliveryTime = null) => new()
    {
        CustomerId = "CUST-101",
        Type = "Email",
        Recipient = recipient,
        Subject = "Order Confirmed",
        Message = message,
        Priority = priority,
        RequestedDeliveryTime = deliveryTime
    };
}
