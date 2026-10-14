using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationApi.Configuration;
using NotificationApi.Dtos;
using NotificationApi.Models;
using NotificationApi.Providers;
using NotificationApi.Repositories;
using NotificationApi.Validation;

namespace NotificationApi.Services;

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;
    private readonly INotificationProvider _provider;
    private readonly INotificationRequestValidator _validator;
    private readonly TimeProvider _clock;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationRepository repository,
        INotificationProvider provider,
        INotificationRequestValidator validator,
        TimeProvider clock,
        IOptions<NotificationOptions> options,
        ILogger<NotificationService> logger)
    {
        _repository = repository;
        _provider = provider;
        _validator = validator;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CreateNotificationResult> CreateAsync(
        CreateNotificationRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();

        var validation = _validator.Validate(request, now, idempotencyKey);
        if (!validation.IsValid)
            return new CreateNotificationResult(null, false, validation.Errors);

        var deliveryTime = request.RequestedDeliveryTime ?? now;

        var notification = new Notification
        {
            CustomerId = request.CustomerId!.Trim(),
            Type = validation.Type,
            Recipient = request.Recipient!.Trim(),
            Subject = request.Subject?.Trim(),
            Message = request.Message!.Trim(),
            Priority = validation.Priority,
            RequestedDeliveryTime = deliveryTime,
            NextAttemptAt = deliveryTime,
            CreatedAt = now,
            Fingerprint = BuildFingerprint(request, validation, idempotencyKey)
        };

        var (stored, added) = await _repository.AddIfNotDuplicateAsync(
            notification, now - _options.DuplicateWindow, ct);

        if (!added)
            _logger.LogInformation("Duplicate request ignored, returning existing notification {Id}", stored.Id);

        return new CreateNotificationResult(stored, IsDuplicate: !added, Errors: null);
    }

    public Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _repository.GetByIdAsync(id, ct);

    public Task<PagedResult<Notification>> GetByCustomerAsync(
        string customerId, NotificationStatus? status, int page, int pageSize, CancellationToken ct = default) =>
        _repository.GetByCustomerAsync(customerId, status, page, pageSize, ct);

    public async Task<IReadOnlyList<Notification>> ProcessPendingAsync(CancellationToken ct = default)
    {
        var batch = await _repository.ClaimDueNotificationsAsync(
            _clock.GetUtcNow(), _options.BatchSize, _options.ProcessingTimeout, _options.MaxAttempts, ct);

        foreach (var notification in batch)
        {
            try
            {
                await _provider.SendAsync(notification, ct);

                notification.Status = NotificationStatus.Sent;
                notification.ProcessedAt = _clock.GetUtcNow();
                notification.ProcessingStartedAt = null;
                notification.FailureReason = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The request was cancelled, it is not a provider problem.
                // Put it back (and do not count the attempt) so it is not stuck in Processing.
                notification.Status = NotificationStatus.Pending;
                notification.ProcessingStartedAt = null;
                notification.AttemptCount--;
                await _repository.UpdateAsync(notification, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                // One bad notification must not stop the rest of the batch.
                _logger.LogError(ex, "Provider failed for notification {Id} (attempt {Attempt}/{Max})",
                    notification.Id, notification.AttemptCount, _options.MaxAttempts);
                HandleFailure(notification, ex.Message);
            }

            await _repository.UpdateAsync(notification, ct);
        }

        return batch;
    }

    // Attempts left  -> back to Pending, try again later (waiting longer each time).
    // No attempts    -> Failed for good.
    private void HandleFailure(Notification notification, string reason)
    {
        var now = _clock.GetUtcNow();
        notification.FailureReason = reason;
        notification.ProcessingStartedAt = null;

        if (notification.AttemptCount >= _options.MaxAttempts)
        {
            notification.Status = NotificationStatus.Failed;
            notification.ProcessedAt = now;
        }
        else
        {
            notification.Status = NotificationStatus.Pending;
            notification.NextAttemptAt = now + _options.RetryDelay(notification.AttemptCount);
        }
    }

    // With an Idempotency-Key the key decides what a duplicate is.
    // Without one, the same business content gives the same fingerprint. Hashed to keep it short.
    private static string BuildFingerprint(CreateNotificationRequest r, ValidationResult v, string? idempotencyKey)
    {
        var raw = idempotencyKey is not null
            ? string.Join("|", "key", r.CustomerId!.Trim().ToLowerInvariant(), idempotencyKey)
            : string.Join("|",
                r.CustomerId!.Trim().ToLowerInvariant(),
                v.Type,
                r.Recipient!.Trim().ToLowerInvariant(),
                r.Subject?.Trim() ?? "",
                r.Message!.Trim(),
                v.Priority,
                r.RequestedDeliveryTime?.UtcDateTime.ToString("O") ?? "");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
