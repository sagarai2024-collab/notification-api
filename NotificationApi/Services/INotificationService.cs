using NotificationApi.Dtos;
using NotificationApi.Models;

namespace NotificationApi.Services;

public record CreateNotificationResult(
    Notification? Notification,
    bool IsDuplicate,
    IReadOnlyDictionary<string, List<string>>? Errors)
{
    public bool IsValid => Errors is null;
}

public interface INotificationService
{
    /// <param name="idempotencyKey">
    /// Optional key chosen by the caller. The same key (per customer) inside the duplicate window
    /// is treated as the same request. Without a key, the request content is compared instead.
    /// </param>
    Task<CreateNotificationResult> CreateAsync(
        CreateNotificationRequest request, string? idempotencyKey = null, CancellationToken ct = default);

    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<Notification>> GetByCustomerAsync(
        string customerId, NotificationStatus? status, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<Notification>> ProcessPendingAsync(CancellationToken ct = default);
}
