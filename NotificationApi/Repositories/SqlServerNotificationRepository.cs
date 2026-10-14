using Microsoft.EntityFrameworkCore;
using NotificationApi.Data;
using NotificationApi.Models;

namespace NotificationApi.Repositories;

// SQL Server repository (EF Core). Registered as scoped because DbContext is scoped.
public class SqlServerNotificationRepository : INotificationRepository
{
    private readonly AppDbContext _db;

    public SqlServerNotificationRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<PagedResult<Notification>> GetByCustomerAsync(
        string customerId, NotificationStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        // The default SQL Server collation is case-insensitive, so "cust-101" matches "CUST-101".
        var query = _db.Notifications.AsNoTracking().Where(n => n.CustomerId == customerId);

        if (status is { } s)
            query = query.Where(n => n.Status == s);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenBy(n => n.Id)   // stable order between pages
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Notification>(items, total);
    }

    public async Task<(Notification Notification, bool Added)> AddIfNotDuplicateAsync(
        Notification notification, DateTimeOffset duplicateSince, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Application lock: two parallel retries of the same request wait for each other,
        // so only one of them can insert. It is released when the transaction ends,
        // and it works across several app instances too.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_getapplock @Resource = N'notification-create', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000",
            ct);

        var existing = await _db.Notifications.FirstOrDefaultAsync(
            n => n.Fingerprint == notification.Fingerprint && n.CreatedAt >= duplicateSince, ct);

        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return (existing, false);
        }

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (notification, true);
    }

    public async Task<IReadOnlyList<Notification>> ClaimDueNotificationsAsync(
        DateTimeOffset now, int maxCount, TimeSpan processingTimeout, int maxAttempts, CancellationToken ct = default)
    {
        var stuckBefore = now - processingTimeout;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // 1) Stuck in Processing and no attempts left: give up.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE Notifications
            SET Status = 'Failed', ProcessedAt = {now}, ProcessingStartedAt = NULL,
                FailureReason = N'Processing did not finish (application stopped?) and no attempts are left.'
            WHERE Status = 'Processing' AND ProcessingStartedAt <= {stuckBefore} AND AttemptCount >= {maxAttempts}", ct);

        // 2) Take the ready ones. UPDLOCK + READPAST: rows locked by another /process call or worker
        //    are skipped, so the same notification is never claimed twice.
        //    ORDER BY gives the processing order: High priority first, then oldest.
        var claimed = await _db.Notifications
            .FromSqlInterpolated($@"
                SELECT TOP ({maxCount}) *
                FROM Notifications WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (Status = 'Pending' AND NextAttemptAt <= {now})
                   OR (Status = 'Processing' AND ProcessingStartedAt <= {stuckBefore} AND AttemptCount < {maxAttempts})
                ORDER BY Priority DESC, CreatedAt ASC")
            .ToListAsync(ct);

        foreach (var n in claimed)
        {
            n.Status = NotificationStatus.Processing;
            n.ProcessingStartedAt = now;
            n.AttemptCount++;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return claimed;
    }

    public async Task UpdateAsync(Notification notification, CancellationToken ct = default)
    {
        // The entity is tracked by this DbContext, so SaveChanges writes its new values.
        await _db.SaveChangesAsync(ct);
    }
}
