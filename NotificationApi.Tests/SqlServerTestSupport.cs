using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NotificationApi.Data;

namespace NotificationApi.Tests;

// Tests that use SQL Server run against a temporary database (own name, deleted afterwards).
// Default server: .\SQLEXPRESS. Override with the environment variable NOTIFICATION_TEST_SQL
// (a connection string WITHOUT "Database="). If no server answers, those tests are skipped, not failed.
public static class TestDatabase
{
    private static readonly string BaseConnection =
        Environment.GetEnvironmentVariable("NOTIFICATION_TEST_SQL")
        ?? @"Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly bool IsAvailable = Check();

    private static bool Check()
    {
        try
        {
            using var connection = new SqlConnection(new SqlConnectionStringBuilder(BaseConnection) { ConnectTimeout = 3 }.ConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string NewConnectionString() =>
        new SqlConnectionStringBuilder(BaseConnection)
        {
            InitialCatalog = "NotificationApi_Test_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;

    public static AppDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>Creates the temporary database by running the real EF migrations.</summary>
    public static async Task CreateAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();
    }

    public static async Task DropAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureDeletedAsync();
    }
}

public sealed class FactIfSqlServerAttribute : FactAttribute
{
    public FactIfSqlServerAttribute()
    {
        if (!TestDatabase.IsAvailable)
            Skip = "SQL Server is not reachable (set NOTIFICATION_TEST_SQL or start .\\SQLEXPRESS).";
    }
}
