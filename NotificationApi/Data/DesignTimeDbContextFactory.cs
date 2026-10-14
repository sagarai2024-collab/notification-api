using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationApi.Data;

// Used only by the "dotnet ef" command line tool (migrations add / script).
// Without it the tool would start the whole web app, which would also apply migrations.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(@"Server=.\SQLEXPRESS;Database=NotificationDb;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new AppDbContext(options);
    }
}
