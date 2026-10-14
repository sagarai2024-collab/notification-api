using NotificationApi.Models;

namespace NotificationApi.Repositories;

public interface INotificationRepository
{
    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Notifications of one customer, newest first, one page at a time. Page starts at 1.</summary>
    Task<PagedResult<Notification>> GetByCustomerAsync(
        string customerId, NotificationStatus? status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Adds the notification unless one with the same fingerprint was created on/after
    /// <paramref name="duplicateSince"/>. The check and the add happen atomically.
    /// Returns the stored notification and whether it was newly added.
    /// </summary>
    Task<(Notification Notification, bool Added)> AddIfNotDuplicateAsync(
        Notification notification, DateTimeOffset duplicateSince, CancellationToken ct = default);

    /// <summary>
    /// Atomically takes notifications that are ready to send and marks them Processing
    /// (AttemptCount + 1). Ready means:
    ///   - Pending and NextAttemptAt has arrived, or
    ///   - stuck in Processing for longer than <paramref name="processingTimeout"/> (app crashed)
    ///     and still has attempts left.
    /// Stuck notifications with no attempts left are marked Failed instead.
    /// Result order: High priority first, then oldest first.
    /// </summary>
    Task<IReadOnlyList<Notification>> ClaimDueNotificationsAsync(
        DateTimeOffset now, int maxCount, TimeSpan processingTimeout, int maxAttempts, CancellationToken ct = default);

    Task UpdateAsync(Notification notification, CancellationToken ct = default);
}
