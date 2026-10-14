namespace NotificationApi.Models;

public enum NotificationType
{
    Email
}

// Numeric values matter: a bigger number is processed first.
public enum NotificationPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum NotificationStatus
{
    Pending,
    Processing,
    Sent,
    Failed
}

public class Notification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string CustomerId { get; init; }
    public NotificationType Type { get; init; }
    public required string Recipient { get; init; }
    public string? Subject { get; init; }
    public required string Message { get; init; }
    public NotificationPriority Priority { get; init; }

    // When the client wants it delivered.
    public DateTimeOffset RequestedDeliveryTime { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    // Used to detect retried (duplicate) requests.
    public required string Fingerprint { get; init; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    // Earliest time we may try to send it. Starts equal to RequestedDeliveryTime,
    // and moves forward (back-off) after a failed attempt.
    public DateTimeOffset NextAttemptAt { get; set; }

    public int AttemptCount { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? FailureReason { get; set; }
}

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
