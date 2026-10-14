using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NotificationApi.Tests;

// Starts the real application (controllers, validation, SQL Server, migrations) in memory.
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = TestDatabase.NewConnectionString();
    private readonly string _apiKey;

    public ApiFactory() : this("") { }
    internal ApiFactory(string apiKey) => _apiKey = apiKey;   // internal: xUnit allows only one public constructor on a fixture

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:NotificationDb", _connectionString);
        builder.UseSetting("Auth:ApiKey", _apiKey);
        builder.UseSetting("Notifications:WorkerEnabled", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && TestDatabase.IsAvailable)
            TestDatabase.DropAsync(_connectionString).GetAwaiter().GetResult();
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ApiTests(ApiFactory factory)
    {
        // The host (and database) is only started when the first request is sent,
        // so skipped tests never touch SQL Server.
        _client = TestDatabase.IsAvailable ? factory.CreateClient() : new HttpClient();
    }

    private static object Body(string customer, string recipient = "customer@example.com", string message = "Hello") => new
    {
        customerId = customer,
        type = "Email",
        recipient,
        subject = "Subject",
        message,
        priority = "Normal"
    };

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>();

    [FactIfSqlServer]
    public async Task Post_valid_request_returns_201_with_location_and_pending_status()
    {
        var response = await _client.PostAsJsonAsync("/api/notifications", Body("API-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var json = await Json(response);
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal("Email", json.GetProperty("type").GetString());
    }

    [FactIfSqlServer]
    public async Task Posting_the_same_request_twice_returns_200_with_the_same_id()
    {
        var first = await Json(await _client.PostAsJsonAsync("/api/notifications", Body("API-2")));
        var secondResponse = await _client.PostAsJsonAsync("/api/notifications", Body("API-2"));

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await Json(secondResponse)).GetProperty("id").GetGuid());
    }

    [FactIfSqlServer]
    public async Task Idempotency_key_header_makes_different_content_a_duplicate()
    {
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/notifications") { Content = JsonContent.Create(Body("API-3", message: "one")) };
        request1.Headers.Add("Idempotency-Key", "order-9");
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/notifications") { Content = JsonContent.Create(Body("API-3", message: "two")) };
        request2.Headers.Add("Idempotency-Key", "order-9");

        var r1 = await _client.SendAsync(request1);
        var r2 = await _client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [FactIfSqlServer]
    public async Task Invalid_request_returns_400_listing_every_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/notifications",
            new { type = "Email", recipient = "not-an-email", priority = "Urgent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Json(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("customerId", out _));
        Assert.True(errors.TryGetProperty("recipient", out _));
        Assert.True(errors.TryGetProperty("message", out _));
        Assert.True(errors.TryGetProperty("priority", out _));
    }

    [FactIfSqlServer]
    public async Task Unknown_id_returns_404()
    {
        var response = await _client.GetAsync($"/api/notifications/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FactIfSqlServer]
    public async Task Get_by_id_returns_what_was_created()
    {
        var created = await Json(await _client.PostAsJsonAsync("/api/notifications", Body("API-4")));

        var response = await _client.GetAsync($"/api/notifications/{created.GetProperty("id").GetGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("API-4", (await Json(response)).GetProperty("customerId").GetString());
    }

    [FactIfSqlServer]
    public async Task Customer_list_is_paged_and_can_be_filtered_by_status()
    {
        for (var i = 0; i < 3; i++)
            await _client.PostAsJsonAsync("/api/notifications", Body("API-5", message: $"m{i}"));

        var page = await Json(await _client.GetAsync("/api/notifications/customer/api-5?page=1&pageSize=2"));
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());

        var failed = await Json(await _client.GetAsync("/api/notifications/customer/API-5?status=Failed"));
        Assert.Equal(0, failed.GetProperty("totalCount").GetInt32());
    }

    [FactIfSqlServer]
    public async Task Customer_list_rejects_bad_paging_and_unknown_status()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/notifications/customer/X?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/notifications/customer/X?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/notifications/customer/X?status=Nope")).StatusCode);
    }

    [FactIfSqlServer]
    public async Task Process_sends_good_notifications_and_schedules_a_retry_for_failing_ones()
    {
        // Own application + database: /process takes everything that is pending,
        // so this test must not share data with the other tests.
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/notifications", Body("API-6", recipient: "good@example.com"));
        await client.PostAsJsonAsync("/api/notifications", Body("API-6", recipient: "fail@example.com"));

        var response = await client.PostAsync("/api/notifications/process", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(1, json.GetProperty("sent").GetInt32());
        Assert.Equal(1, json.GetProperty("willRetry").GetInt32());
        Assert.Equal(0, json.GetProperty("failed").GetInt32());
    }

    [FactIfSqlServer]
    public async Task Api_key_is_required_only_when_configured()
    {
        await using var secured = new ApiFactory("secret-key");
        var client = secured.CreateClient();

        var noKey = await client.GetAsync($"/api/notifications/{Guid.NewGuid()}");
        var wrongKey = new HttpRequestMessage(HttpMethod.Get, $"/api/notifications/{Guid.NewGuid()}");
        wrongKey.Headers.Add("X-Api-Key", "nope");
        var rightKey = new HttpRequestMessage(HttpMethod.Get, $"/api/notifications/{Guid.NewGuid()}");
        rightKey.Headers.Add("X-Api-Key", "secret-key");

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(rightKey)).StatusCode);   // passed auth, id just doesn't exist
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);   // docs stay open
    }
}
