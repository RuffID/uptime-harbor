using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class ServerUniqueConstraintTests
{
    [Fact]
    public async Task DuplicateConfiguredNameIgnoringCase_IsRejectedByDatabase()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<UptimeHarborDbContext> options =
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseSqlite(connection)
                .Options;
        await using UptimeHarborDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.MonitoredServers.AddRange(
            new MonitoredServer(
                Guid.NewGuid(),
                "Production server",
                "https://first.example/health",
                null,
                true),
            new MonitoredServer(
                Guid.NewGuid(),
                "production SERVER",
                "https://second.example/health",
                null,
                true));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
