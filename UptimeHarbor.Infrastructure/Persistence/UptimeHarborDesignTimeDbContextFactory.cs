using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UptimeHarbor.Infrastructure.Persistence;

public class UptimeHarborDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<UptimeHarborDbContext>
{
    public UptimeHarborDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<UptimeHarborDbContext> options =
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseNpgsql()
                .Options;

        return new UptimeHarborDbContext(options);
    }
}
