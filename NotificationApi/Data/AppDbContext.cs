using Microsoft.EntityFrameworkCore;
using NotificationApi.Models;

namespace NotificationApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");
            e.HasKey(n => n.Id);

            e.Property(n => n.CustomerId).IsRequired().HasMaxLength(100);
            e.Property(n => n.Recipient).IsRequired().HasMaxLength(320);
            e.Property(n => n.Subject).HasMaxLength(200);
            e.Property(n => n.Message).IsRequired().HasMaxLength(5000);
            e.Property(n => n.Fingerprint).IsRequired().HasMaxLength(64).IsUnicode(false);
            e.Property(n => n.FailureReason).HasMaxLength(1000);

            // Enums are saved as text so the table is easy to read in SSMS.
            e.Property(n => n.Type).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            // Priority stays a number: High(2) > Normal(1) > Low(0), so ORDER BY works in SQL.

            // Date-only copy (e.g. 2030-10-06) maintained by SQL Server itself, for easy reading in SSMS.
            // The full value with time stays in RequestedDeliveryTime.
            e.Property<DateOnly>("RequestedDeliveryDate")
                .HasComputedColumnSql("CONVERT(date, [RequestedDeliveryTime])", stored: true);

            e.HasIndex(n => n.CustomerId);
            e.HasIndex(n => new { n.Fingerprint, n.CreatedAt });
            e.HasIndex(n => new { n.Status, n.NextAttemptAt });
        });
    }
}
