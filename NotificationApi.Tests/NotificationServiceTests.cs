using NotificationApi.Configuration;
using NotificationApi.Dtos;
using NotificationApi.Models;
using NotificationApi.Services;

namespace NotificationApi.Tests;

public class NotificationServiceTests
{
    private readonly ServiceFixture _f = new();

    private Task<Notification?> Reload(CreateNotificationResult r) => _f.Service.GetByIdAsync(r.Notification!.Id);

    // ---------- create / store ----------

    [Fact]
    public async Task Create_stores_notification_as_pending_with_id_and_timestamp()
    {
        var result = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        Assert.True(result.IsValid);
        Assert.False(result.IsDuplicate);
        Assert.NotEqual(Guid.Empty, result.Notification!.Id);
        Assert.Equal(NotificationStatus.Pending, result.Notification.Status);
        Assert.Equal(ServiceFixture.Start, result.Notification.CreatedAt);
        Assert.Equal(0, result.Notification.AttemptCount);
    }

    [Fact]
    public async Task Create_with_invalid_request_stores_nothing()
    {
        var result = await _f.Service.CreateAsync(new CreateNotificationRequest());

        Assert.False(result.IsValid);
        Assert.Empty((await _f.Service.GetByCustomerAsync("CUST-101", null, 1, 20)).Items);
    }

    [Fact]
    public async Task Too_long_idempotency_key_is_rejected()
    {
        var result = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(), new string('k', 101));

        Assert.Contains("idempotencyKey", result.Errors!.Keys);
    }

    // ---------- duplicates ----------

    [Fact]
    public async Task Same_request_twice_returns_the_same_notification_and_sends_once()
    {
        var first = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());
        _f.Clock.Advance(TimeSpan.FromSeconds(10));
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Notification!.Id, second.Notification!.Id);

        await _f.Service.ProcessPendingAsync();
        Assert.Single(_f.Provider.Sent);
    }

    [Fact]
    public async Task Duplicate_check_ignores_case_and_spaces_in_recipient()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "customer@example.com"));
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "  Customer@Example.com "));

        Assert.True(second.IsDuplicate);
    }

    [Fact]
    public async Task Different_message_is_not_a_duplicate()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "one"));
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "two"));

        Assert.False(second.IsDuplicate);
    }

    [Fact]
    public async Task Same_request_after_the_duplicate_window_is_accepted_again()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest());
        _f.Clock.Advance(_f.Options.DuplicateWindow + TimeSpan.FromSeconds(1));
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        Assert.False(second.IsDuplicate);
    }

    [Fact]
    public async Task Duplicate_window_comes_from_configuration()
    {
        var f = new ServiceFixture(new NotificationOptions { DuplicateWindowMinutes = 1 });
        await f.Service.CreateAsync(ServiceFixture.ValidRequest());
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False((await f.Service.CreateAsync(ServiceFixture.ValidRequest())).IsDuplicate);
    }

    // ---------- idempotency key ----------

    [Fact]
    public async Task Same_idempotency_key_is_a_duplicate_even_if_the_content_changed()
    {
        var first = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "one"), "order-55");
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "two"), "order-55");

        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Notification!.Id, second.Notification!.Id);
    }

    [Fact]
    public async Task Different_idempotency_keys_are_not_duplicates_even_with_identical_content()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(), "order-1");
        var second = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(), "order-2");

        Assert.False(second.IsDuplicate);
    }

    [Fact]
    public async Task Same_idempotency_key_for_a_different_customer_is_not_a_duplicate()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(), "order-1");
        var other = ServiceFixture.ValidRequest();
        other.CustomerId = "CUST-999";

        Assert.False((await _f.Service.CreateAsync(other, "order-1")).IsDuplicate);
    }

    // ---------- retrieval ----------

    [Fact]
    public async Task Get_unknown_id_returns_null()
    {
        Assert.Null(await _f.Service.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Get_by_customer_returns_only_that_customers_notifications()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "mine"));
        var other = ServiceFixture.ValidRequest(message: "theirs");
        other.CustomerId = "CUST-999";
        await _f.Service.CreateAsync(other);

        var page = await _f.Service.GetByCustomerAsync("CUST-101", null, 1, 20);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("mine", page.Items[0].Message);
    }

    [Fact]
    public async Task Get_by_customer_is_paged_newest_first()
    {
        for (var i = 1; i <= 5; i++)
        {
            await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: $"m{i}"));
            _f.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var page1 = await _f.Service.GetByCustomerAsync("CUST-101", null, 1, 2);
        var page3 = await _f.Service.GetByCustomerAsync("CUST-101", null, 3, 2);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(new[] { "m5", "m4" }, page1.Items.Select(n => n.Message));
        Assert.Equal(new[] { "m1" }, page3.Items.Select(n => n.Message));
    }

    [Fact]
    public async Task Get_by_customer_can_filter_by_status()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "will be sent"));
        await _f.Service.ProcessPendingAsync();
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(message: "still waiting"));

        var sent = await _f.Service.GetByCustomerAsync("CUST-101", NotificationStatus.Sent, 1, 20);
        var pending = await _f.Service.GetByCustomerAsync("CUST-101", NotificationStatus.Pending, 1, 20);

        Assert.Equal("will be sent", Assert.Single(sent.Items).Message);
        Assert.Equal("still waiting", Assert.Single(pending.Items).Message);
    }

    // ---------- processing ----------

    [Fact]
    public async Task High_priority_is_processed_before_normal_and_low()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "low@example.com", priority: "Low"));
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "normal@example.com", priority: "Normal"));
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "high@example.com", priority: "High"));

        await _f.Service.ProcessPendingAsync();

        Assert.Equal(
            new[] { "high@example.com", "normal@example.com", "low@example.com" },
            _f.Provider.Sent.Select(n => n.Recipient));
    }

    [Fact]
    public async Task Same_priority_is_processed_oldest_first()
    {
        foreach (var name in new[] { "a", "b", "c" })
        {
            await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: $"{name}@example.com", priority: "High"));
            _f.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        await _f.Service.ProcessPendingAsync();

        Assert.Equal(
            new[] { "a@example.com", "b@example.com", "c@example.com" },
            _f.Provider.Sent.Select(n => n.Recipient));
    }

    [Fact]
    public async Task Successful_send_marks_notification_as_sent()
    {
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        await _f.Service.ProcessPendingAsync();

        var stored = await Reload(created);
        Assert.Equal(NotificationStatus.Sent, stored!.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.ProcessedAt);
    }

    [Fact]
    public async Task Notification_due_in_the_future_waits_until_its_time()
    {
        var due = ServiceFixture.Start.AddHours(1);
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(deliveryTime: due));

        Assert.Empty(await _f.Service.ProcessPendingAsync());
        Assert.Equal(NotificationStatus.Pending, (await Reload(created))!.Status);

        _f.Clock.Advance(TimeSpan.FromHours(1));   // exactly due
        Assert.Single(await _f.Service.ProcessPendingAsync());
    }

    [Fact]
    public async Task Processing_twice_does_not_send_the_same_notification_again()
    {
        await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        await _f.Service.ProcessPendingAsync();
        var second = await _f.Service.ProcessPendingAsync();

        Assert.Empty(second);
        Assert.Single(_f.Provider.Sent);
    }

    // ---------- provider failure + retry ----------

    [Fact]
    public async Task First_failure_goes_back_to_pending_and_does_not_stop_the_other_notifications()
    {
        _f.Provider.FailFor.Add("bad@example.com");
        var bad = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "bad@example.com", priority: "High"));
        var good = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "good@example.com", priority: "Low"));

        var processed = await _f.Service.ProcessPendingAsync();

        Assert.Equal(2, processed.Count);
        var retrying = await Reload(bad);
        Assert.Equal(NotificationStatus.Pending, retrying!.Status);
        Assert.Equal(1, retrying.AttemptCount);
        Assert.Equal("provider down", retrying.FailureReason);
        Assert.Equal(NotificationStatus.Sent, (await Reload(good))!.Status);
    }

    [Fact]
    public async Task Retry_waits_for_the_back_off_delay_and_it_doubles_each_time()
    {
        _f.Provider.FailFor.Add("bad@example.com");
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "bad@example.com"));

        await _f.Service.ProcessPendingAsync();                       // attempt 1 fails, wait 30s
        _f.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(await _f.Service.ProcessPendingAsync());         // too early

        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await _f.Service.ProcessPendingAsync());        // attempt 2 fails, wait 60s

        _f.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(await _f.Service.ProcessPendingAsync());
        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await _f.Service.ProcessPendingAsync());        // attempt 3

        Assert.Equal(3, (await Reload(created))!.AttemptCount);
    }

    [Fact]
    public async Task After_the_last_attempt_the_notification_is_failed_for_good()
    {
        _f.Provider.FailFor.Add("bad@example.com");
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "bad@example.com"));

        for (var i = 0; i < _f.Options.MaxAttempts; i++)
        {
            await _f.Service.ProcessPendingAsync();
            _f.Clock.Advance(TimeSpan.FromHours(1));
        }

        var stored = await Reload(created);
        Assert.Equal(NotificationStatus.Failed, stored!.Status);
        Assert.Equal(_f.Options.MaxAttempts, stored.AttemptCount);
        Assert.NotNull(stored.ProcessedAt);

        // And it is never tried again.
        var callsBefore = _f.Provider.Calls;
        await _f.Service.ProcessPendingAsync();
        Assert.Equal(callsBefore, _f.Provider.Calls);
    }

    [Fact]
    public async Task Retry_succeeds_when_the_provider_recovers()
    {
        _f.Provider.FailFor.Add("flaky@example.com");
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "flaky@example.com"));

        await _f.Service.ProcessPendingAsync();            // fails
        _f.Provider.FailFor.Clear();                       // provider is back
        _f.Clock.Advance(TimeSpan.FromSeconds(30));
        await _f.Service.ProcessPendingAsync();

        var stored = await Reload(created);
        Assert.Equal(NotificationStatus.Sent, stored!.Status);
        Assert.Equal(2, stored.AttemptCount);
        Assert.Null(stored.FailureReason);
    }

    [Fact]
    public async Task Max_attempts_of_one_means_no_retry()
    {
        var f = new ServiceFixture(new NotificationOptions { MaxAttempts = 1 });
        f.Provider.FailFor.Add("bad@example.com");
        var created = await f.Service.CreateAsync(ServiceFixture.ValidRequest(recipient: "bad@example.com"));

        await f.Service.ProcessPendingAsync();

        Assert.Equal(NotificationStatus.Failed, (await f.Service.GetByIdAsync(created.Notification!.Id))!.Status);
    }

    // ---------- stuck in Processing (app crashed) ----------

    [Fact]
    public async Task Notification_stuck_in_processing_is_picked_up_again_after_the_timeout()
    {
        var created = await _f.Service.CreateAsync(ServiceFixture.ValidRequest());

        // Simulate a crash: the notification was claimed but the app died before sending.
        await _f.Repository.ClaimDueNotificationsAsync(_f.Clock.GetUtcNow(), 10, _f.Options.ProcessingTimeout, _f.Options.MaxAttempts);

        _f.Clock.Advance(_f.Options.ProcessingTimeout - TimeSpan.FromSeconds(1));
        Assert.Empty(await _f.Service.ProcessPendingAsync());          // maybe still running, leave it alone

        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await _f.Service.ProcessPendingAsync());         // timed out, take it again

        var stored = await Reload(created);
        Assert.Equal(NotificationStatus.Sent, stored!.Status);
        Assert.Equal(2, stored.AttemptCount);
    }

    [Fact]
    public async Task Stuck_notification_with_no_attempts_left_is_marked_failed()
    {
        var f = new ServiceFixture(new NotificationOptions { MaxAttempts = 1 });
        var created = await f.Service.CreateAsync(ServiceFixture.ValidRequest());
        await f.Repository.ClaimDueNotificationsAsync(f.Clock.GetUtcNow(), 10, f.Options.ProcessingTimeout, f.Options.MaxAttempts);

        f.Clock.Advance(f.Options.ProcessingTimeout);
        Assert.Empty(await f.Service.ProcessPendingAsync());

        var stored = await f.Service.GetByIdAsync(created.Notification!.Id);
        Assert.Equal(NotificationStatus.Failed, stored!.Status);
        Assert.Equal(0, f.Provider.Calls);
    }
}
