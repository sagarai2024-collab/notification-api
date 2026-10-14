using NotificationApi.Models;

namespace NotificationApi.Dtos;

public record NotificationResponse(
    Guid Id,
    string CustomerId,
    NotificationType Type,
    string Recipient,
    string? Subject,
    string Message,
    NotificationPriority Priority,
    DateTimeOffset RequestedDeliveryTime,
    DateTimeOffset CreatedAt,
    NotificationStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? ProcessedAt,
    string? FailureReason)
{
    public static NotificationResponse From(Notification n) => new(
        n.Id, n.CustomerId, n.Type, n.Recipient, n.Subject, n.Message, n.Priority,
        n.RequestedDeliveryTime, n.CreatedAt, n.Status, n.AttemptCount,
        // Only meaningful while it is still waiting to be (re)sent.
        n.Status == NotificationStatus.Pending ? n.NextAttemptAt : null,
        n.ProcessedAt, n.FailureReason);
}

public record ProcessNotificationsResponse(
    int Total,
    int Sent,
    int Failed,
    int WillRetry,
    IReadOnlyList<NotificationResponse> Notifications);

public record PagedResponse<T>(int Page, int PageSize, int TotalCount, IReadOnlyList<T> Items);
