using Microsoft.EntityFrameworkCore;
using NotificationApi.Data;
using NotificationApi.Models;
using NotificationApi.Repositories;

namespace NotificationApi.Tests;

// Real SQL Server. xUnit makes a new instance per test, so every test gets its own empty database.
public class SqlServerRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 3;

    private readonly string _connectionString = TestDatabase.NewConnectionString();

    public async Task InitializeAsync()
    {
        if (TestDatabase.IsAvailable)
            await TestDatabase.CreateAsync(_connectionString);
    }

    public async Task DisposeAsync()
    {
        if (TestDatabase.IsAvailable)
            await TestDatabase.DropAsync(_connectionString);
    }

    // Every call uses a new DbContext, like a new HTTP request would.
    private (SqlServerNotificationRepository Repo, AppDbContext Db) NewRepo()
    {
        var db = TestDatabase.CreateContext(_connectionString);
        return (new SqlServerNotificationRepository(db), db);
    }

    private static Notification Make(
        string recipient = "a@example.com", NotificationPriority priority = NotificationPriority.Normal,
        string customer = "CUST-1", string? fingerprint = null, DateTimeOffset? createdAt = null,
        DateTimeOffset? nextAttempt = null) => new()
    {
        CustomerId = customer,
        Type = NotificationType.Email,
        Recipient = recipient,
        Message = "hello",
        Priority = priority,
        Fingerprint = fingerprint ?? Guid.NewGuid().ToString("N"),
        CreatedAt = createdAt ?? Now,
        RequestedDeliveryTime = nextAttempt ?? Now,
        NextAttemptAt = nextAttempt ?? Now
    };

    private async Task<Notification> Save(Notification n)
    {
        var (repo, db) = NewRepo();
        await using (db)
            await repo.AddIfNotDuplicateAsync(n, DateTimeOffset.MinValue.AddYears(1));
        return n;
    }

    private async Task<Notification> Reload(Guid id)
    {
        var (repo, db) = NewRepo();
        await using (db)
            return (await repo.GetByIdAsync(id))!;
    }

    [FactIfSqlServer]
    public async Task Saved_notification_can_be_read_back_with_all_values()
    {
        var saved = await Save(Make(recipient: "x@example.com", priority: NotificationPriority.High));

        var loaded = await Reload(saved.Id);

        Assert.Equal("x@example.com", loaded.Recipient);
        Assert.Equal(NotificationPriority.High, loaded.Priority);
        Assert.Equal(NotificationStatus.Pending, loaded.Status);
        Assert.Equal(Now, loaded.CreatedAt);
    }

    [FactIfSqlServer]
    public async Task Date_only_column_is_filled_by_the_database()
    {
        var saved = await Save(Make(nextAttempt: new DateTimeOffset(2030, 10, 6, 0, 0, 0, TimeSpan.FromHours(5.5))));

        var (_, db) = NewRepo();
        await using (db)
        {
            var n = await db.Notifications.SingleAsync(x => x.Id == saved.Id);
            Assert.Equal(new DateOnly(2030, 10, 6), db.Entry(n).Property<DateOnly>("RequestedDeliveryDate").CurrentValue);
        }
    }

    [FactIfSqlServer]
    public async Task Ten_parallel_identical_requests_create_exactly_one_row()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var (repo, db) = NewRepo();
            await using (db)
                return await repo.AddIfNotDuplicateAsync(Make(fingerprint: "SAME"), Now.AddMinutes(-5));
        }));

        Assert.Equal(1, results.Count(r => r.Added));
        Assert.Single(results.Select(r => r.Notification.Id).Distinct());

        var (_, db2) = NewRepo();
        await using (db2)
            Assert.Equal(1, await db2.Notifications.CountAsync());
    }

    [FactIfSqlServer]
    public async Task Same_fingerprint_outside_the_window_is_stored_again()
    {
        await Save(Make(fingerprint: "SAME", createdAt: Now.AddMinutes(-10)));

        var (repo, db) = NewRepo();
        await using (db)
        {
            var (_, added) = await repo.AddIfNotDuplicateAsync(Make(fingerprint: "SAME"), Now.AddMinutes(-5));
            Assert.True(added);
        }
    }

    [FactIfSqlServer]
    public async Task Claim_returns_high_first_then_oldest_and_marks_rows_processing()
    {
        var low = await Save(Make("low@x.com", NotificationPriority.Low, createdAt: Now.AddSeconds(-30)));
        var normal = await Save(Make("normal@x.com", NotificationPriority.Normal, createdAt: Now.AddSeconds(-20)));
        var highOld = await Save(Make("high1@x.com", NotificationPriority.High, createdAt: Now.AddSeconds(-10)));
        var highNew = await Save(Make("high2@x.com", NotificationPriority.High, createdAt: Now.AddSeconds(-5)));

        var (repo, db) = NewRepo();
        IReadOnlyList<Notification> claimed;
        await using (db)
            claimed = await repo.ClaimDueNotificationsAsync(Now, 100, Timeout, MaxAttempts);

        Assert.Equal(new[] { highOld.Id, highNew.Id, normal.Id, low.Id }, claimed.Select(n => n.Id));

        var stored = await Reload(highOld.Id);
        Assert.Equal(NotificationStatus.Processing, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(Now, stored.ProcessingStartedAt);
    }

    [FactIfSqlServer]
    public async Task Claim_skips_notifications_that_are_not_due_yet()
    {
        await Save(Make("later@x.com", nextAttempt: Now.AddMinutes(1)));
        var due = await Save(Make("now@x.com", nextAttempt: Now));

        var (repo, db) = NewRepo();
        await using (db)
        {
            var claimed = await repo.ClaimDueNotificationsAsync(Now, 100, Timeout, MaxAttempts);
            Assert.Equal(due.Id, Assert.Single(claimed).Id);
        }
    }

    [FactIfSqlServer]
    public async Task Respects_the_batch_size_limit()
    {
        for (var i = 0; i < 5; i++)
            await Save(Make($"n{i}@x.com"));

        var (repo, db) = NewRepo();
        await using (db)
            Assert.Equal(2, (await repo.ClaimDueNotificationsAsync(Now, 2, Timeout, MaxAttempts)).Count);
    }

    [FactIfSqlServer]
    public async Task Parallel_claims_never_take_the_same_notification_twice()
    {
        for (var i = 0; i < 20; i++)
            await Save(Make($"n{i}@x.com"));

        var batches = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            var (repo, db) = NewRepo();
            await using (db)
                return await repo.ClaimDueNotificationsAsync(Now, 100, Timeout, MaxAttempts);
        }));

        var ids = batches.SelectMany(b => b.Select(n => n.Id)).ToList();
        Assert.Equal(20, ids.Count);                       // every notification was taken...
        Assert.Equal(20, ids.Distinct().Count());          // ...exactly once
    }

    [FactIfSqlServer]
    public async Task Stuck_processing_row_is_claimed_again_only_after_the_timeout()
    {
        var saved = await Save(Make());
        var (repo1, db1) = NewRepo();
        await using (db1)
            await repo1.ClaimDueNotificationsAsync(Now, 10, Timeout, MaxAttempts);   // "app crashes" after this

        var (repo2, db2) = NewRepo();
        await using (db2)
        {
            Assert.Empty(await repo2.ClaimDueNotificationsAsync(Now.Add(Timeout).AddSeconds(-1), 10, Timeout, MaxAttempts));

            var again = await repo2.ClaimDueNotificationsAsync(Now.Add(Timeout), 10, Timeout, MaxAttempts);
            var n = Assert.Single(again);
            Assert.Equal(saved.Id, n.Id);
            Assert.Equal(2, n.AttemptCount);
        }
    }

    [FactIfSqlServer]
    public async Task Stuck_row_without_attempts_left_becomes_failed()
    {
        var saved = await Save(Make());
        var (repo1, db1) = NewRepo();
        await using (db1)
            await repo1.ClaimDueNotificationsAsync(Now, 10, Timeout, maxAttempts: 1);

        var (repo2, db2) = NewRepo();
        await using (db2)
            Assert.Empty(await repo2.ClaimDueNotificationsAsync(Now.Add(Timeout), 10, Timeout, maxAttempts: 1));

        var stored = await Reload(saved.Id);
        Assert.Equal(NotificationStatus.Failed, stored.Status);
        Assert.NotNull(stored.FailureReason);
    }

    [FactIfSqlServer]
    public async Task Customer_query_is_case_insensitive_paged_and_filterable()
    {
        for (var i = 0; i < 5; i++)
            await Save(Make($"n{i}@x.com", customer: "CUST-1", createdAt: Now.AddSeconds(i)));
        await Save(Make("other@x.com", customer: "CUST-2"));

        var (repo, db) = NewRepo();
        await using (db)
        {
            var page = await repo.GetByCustomerAsync("cust-1", null, page: 2, pageSize: 2);
            Assert.Equal(5, page.TotalCount);
            Assert.Equal(new[] { "n2@x.com", "n1@x.com" }, page.Items.Select(n => n.Recipient));   // newest first

            var sent = await repo.GetByCustomerAsync("CUST-1", NotificationStatus.Sent, 1, 20);
            Assert.Equal(0, sent.TotalCount);
        }
    }

    [FactIfSqlServer]
    public async Task Update_saves_new_status_and_failure_reason()
    {
        var saved = await Save(Make());

        var (repo, db) = NewRepo();
        await using (db)
        {
            var n = (await repo.GetByIdAsync(saved.Id))!;
            n.Status = NotificationStatus.Failed;
            n.FailureReason = "boom";
            await repo.UpdateAsync(n);
        }

        var stored = await Reload(saved.Id);
        Assert.Equal(NotificationStatus.Failed, stored.Status);
        Assert.Equal("boom", stored.FailureReason);
    }
}
