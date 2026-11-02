using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.Queries;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class MaterializedReadQueryTests
{
    [Fact]
    public async Task DashboardQuery_MaterializesReadRecordsWithoutEndpointUrl()
    {
        // Arrange
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<UptimeHarborDbContext> options = new DbContextOptionsBuilder<UptimeHarborDbContext>()
            .UseSqlite(connection)
            .Options;
        Guid userId = Guid.NewGuid();
        MonitoredServer visibleServer = new(
            Guid.NewGuid(),
            "Visible server",
            "https://secret-server.example/health",
            "Visible server",
            true);
        MonitoredServer hiddenServer = new(
            Guid.NewGuid(),
            "Hidden server",
            "https://hidden-server.example/health",
            "Hidden server",
            true);
        MonitoredServer visibleContainer = MonitoredServer.CreateApplication(
            Guid.NewGuid(),
            visibleServer.Id,
            "https://secret-container.example/health",
            "Visible container",
            true);
        DashboardReadSnapshot snapshot;

        await using (UptimeHarborDbContext context = new(options))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.Users.Add(new ApplicationUser(userId, "user"));
            context.MonitoredServers.AddRange(visibleServer, hiddenServer);
            context.MonitoredServers.Add(visibleContainer);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            DashboardSnapshotReadQuery query = new(context);
            // Act
            snapshot = await query.GetSnapshotAsync(
                userId,
                isAdministrator: true,
                TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(2, snapshot.Servers.Count);
        Assert.Single(snapshot.Containers);
        Assert.Empty(snapshot.PermittedServerIds);
        Assert.Empty(snapshot.PermittedContainerIds);
        Assert.DoesNotContain(
            typeof(DashboardServerReadRecord).GetProperties(),
            property => property.Name.Contains("Endpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(DashboardContainerReadRecord).GetProperties(),
            property => property.Name.Contains("Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecipientQuery_LoadsAdministratorRecipientsInOneBatchQuery()
    {
        // Arrange
        CountingCommandInterceptor interceptor = new();
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<UptimeHarborDbContext> options = new DbContextOptionsBuilder<UptimeHarborDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        Guid administratorId = Guid.NewGuid();
        Guid permittedUserId = Guid.NewGuid();
        Guid hiddenUserId = Guid.NewGuid();
        Guid disabledUserId = Guid.NewGuid();
        MonitoredServer server = new(
            Guid.NewGuid(),
            "Server",
            "https://server.example/health",
            null,
            true);
        MonitoredServer container = MonitoredServer.CreateApplication(
            Guid.NewGuid(),
            server.Id,
            "https://container.example/health",
            null,
            true);

        await using UptimeHarborDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        ApplicationUser administrator = new(administratorId, "administrator");
        ApplicationUser permittedUser = new(permittedUserId, "permitted");
        ApplicationUser hiddenUser = new(hiddenUserId, "hidden");
        ApplicationUser disabledUser = new(disabledUserId, "disabled");
        disabledUser.Disable();
        ApplicationRole administratorRole = new(Guid.NewGuid(), ApplicationRoles.ADMINISTRATOR)
        {
            NormalizedName = ApplicationRoles.ADMINISTRATOR.ToUpperInvariant()
        };
        context.Users.AddRange(administrator, permittedUser, hiddenUser, disabledUser);
        context.Roles.Add(administratorRole);
        foreach (Guid userId in new[] { administratorId, permittedUserId, hiddenUserId, disabledUserId })
        {
            context.UserRoles.Add(new IdentityUserRole<Guid>
            {
                UserId = userId,
                RoleId = administratorRole.Id
            });
        }

        context.MonitoredServers.Add(server);
        context.MonitoredServers.Add(container);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        interceptor.Reset();

        MonitoringRecipientReadQuery query = new(context);
        // Act
        IReadOnlyList<MonitoringRecipientReadRecord> candidates = await query.GetCandidatesAsync(
            MonitoringObjectKind.Container,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Equal(4, candidates.Count);
        Assert.True(candidates.Single(candidate => candidate.UserId == administratorId).IsAdministrator);
        Assert.All(candidates, candidate => Assert.Empty(candidate.PermittedContainerIds));

        // Arrange
        MonitoringEventRecipientResolver resolver = new(query);
        // Act
        IReadOnlyList<Guid> recipients = await resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            container.Id,
            server.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(administratorId, recipients);
        Assert.Contains(permittedUserId, recipients);
        Assert.Contains(hiddenUserId, recipients);
        Assert.DoesNotContain(disabledUserId, recipients);
    }

    private class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public void Reset()
        {
            ReaderCommandCount = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
