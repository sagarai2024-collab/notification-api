using NotificationApi.Models;

namespace NotificationApi.Repositories;

// Used by the fast unit tests. The real application uses SqlServerNotificationRepository.
// One lock keeps the logic easy to reason about.
public class InMemoryNotificationRepository : INotificationRepository
{
    private readonly List<Notification> _items = new();
    private readonly object _lock = new();

    public Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_items.FirstOrDefault(n => n.Id == id));
    }

    public Task<PagedResult<Notification>> GetByCustomerAsync(
        string customerId, NotificationStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var query = _items.Where(n => string.Equals(n.CustomerId, customerId, StringComparison.OrdinalIgnoreCase));
            if (status is { } s)
                query = query.Where(n => n.Status == s);

            var all = query.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).ToList();
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<Notification>(items, all.Count));
        }
    }

    public Task<(Notification Notification, bool Added)> AddIfNotDuplicateAsync(
        Notification notification, DateTimeOffset duplicateSince, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(n =>
                n.Fingerprint == notification.Fingerprint && n.CreatedAt >= duplicateSince);

            if (existing is not null)
                return Task.FromResult((existing, false));

            _items.Add(notification);
            return Task.FromResult((notification, true));
        }
    }

    public Task<IReadOnlyList<Notification>> ClaimDueNotificationsAsync(
        DateTimeOffset now, int maxCount, TimeSpan processingTimeout, int maxAttempts, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var stuckBefore = now - processingTimeout;

            bool IsStuck(Notification n) =>
                n.Status == NotificationStatus.Processing && n.ProcessingStartedAt <= stuckBefore;

            // Stuck and out of attempts: give up.
            foreach (var n in _items.Where(n => IsStuck(n) && n.AttemptCount >= maxAttempts))
            {
                n.Status = NotificationStatus.Failed;
                n.FailureReason = "Processing did not finish (application stopped?) and no attempts are left.";
                n.ProcessedAt = now;
                n.ProcessingStartedAt = null;
            }

            var claimed = _items
                .Where(n => (n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now)
                            || (IsStuck(n) && n.AttemptCount < maxAttempts))
                .OrderByDescending(n => n.Priority)   // High -> Low
                .ThenBy(n => n.CreatedAt)             // same priority: first come, first served
                .Take(maxCount)
                .ToList();

            foreach (var n in claimed)
            {
                n.Status = NotificationStatus.Processing;
                n.ProcessingStartedAt = now;
                n.AttemptCount++;
            }

            return Task.FromResult<IReadOnlyList<Notification>>(claimed);
        }
    }

    public Task UpdateAsync(Notification notification, CancellationToken ct = default)
    {
        // Objects are stored by reference, so changes are already visible.
        return Task.CompletedTask;
    }
}
