using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationApi.Configuration;
using NotificationApi.Providers;
using NotificationApi.Repositories;
using NotificationApi.Services;
using NotificationApi.Validation;

namespace NotificationApi.Tests;

public class WorkerTests
{
    [Fact]
    public async Task Worker_sends_pending_notifications_without_anyone_calling_process()
    {
        var provider = new FakeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INotificationRepository>(new InMemoryNotificationRepository());
        services.AddSingleton<INotificationProvider>(provider);
        services.AddSingleton<INotificationRequestValidator, NotificationRequestValidator>();
        services.AddScoped<INotificationService, NotificationService>();
        services.Configure<NotificationOptions>(o => o.WorkerIntervalSeconds = 1);
        await using var root = services.BuildServiceProvider();

        // Create a notification the normal way.
        using (var scope = root.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<INotificationService>()
                .CreateAsync(ServiceFixture.ValidRequest());
            Assert.True(result.IsValid);
        }

        var worker = new NotificationProcessingWorker(
            root.GetRequiredService<IServiceScopeFactory>(),
            root.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>(),
            NullLogger<NotificationProcessingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Wait (up to 5 s) for the worker's next tick to send it.
            for (var i = 0; i < 50 && provider.Sent.Count == 0; i++)
                await Task.Delay(100);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Single(provider.Sent);
    }
}
