using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class TelegramUniqueConstraintTests
{
    [Fact]
    public async Task ConcurrentDuplicateChat_IsRejectedByProviderUniqueIndex()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await using UptimeHarborDbContext firstContext = CreateContext(connection);
        await using UptimeHarborDbContext secondContext = CreateContext(connection);
        firstContext.TelegramChats.Add(new TelegramChat(Guid.NewGuid(), -100123, "Первый"));
        secondContext.TelegramChats.Add(new TelegramChat(Guid.NewGuid(), -100123, "Второй"));

        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentDuplicateBinding_IsRejectedByProviderUniqueIndex()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        Guid serverId = Guid.NewGuid();
        Guid chatId = Guid.NewGuid();
        await using (UptimeHarborDbContext setupContext = CreateContext(connection))
        {
            setupContext.MonitoredServers.Add(new MonitoredServer(
                serverId,
                "Server",
                "https://server.example/health",
                null,
                true));
            setupContext.TelegramChats.Add(new TelegramChat(chatId, -100123, "Операторы"));
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using UptimeHarborDbContext firstContext = CreateContext(connection);
        await using UptimeHarborDbContext secondContext = CreateContext(connection);
        firstContext.NotificationBindings.Add(NotificationBinding.ForServer(
            Guid.NewGuid(),
            chatId,
            serverId));
        secondContext.NotificationBindings.Add(NotificationBinding.ForServer(
            Guid.NewGuid(),
            chatId,
            serverId));

        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using UptimeHarborDbContext context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static UptimeHarborDbContext CreateContext(SqliteConnection connection)
    {
        return new UptimeHarborDbContext(
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseSqlite(connection)
                .Options);
    }
}
