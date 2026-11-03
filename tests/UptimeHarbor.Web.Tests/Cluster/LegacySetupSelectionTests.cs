using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class LegacySetupSelectionTests
{
    [Fact]
    public async Task DisabledTestNotificationUsesLegacyRepositoryWithoutResolvingClusterPending()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=unused.invalid;Database=unused;Integrated Security=true;Encrypt=true"
        }).Build());
        Store store = new();
        services.AddScoped<IAdministratorAccessPolicy, Allowed>();
        services.AddSingleton<INotificationSettingsUnitOfWork>(store);
        services.AddScoped<IAcceptedTestDelivery>(_ => throw new InvalidOperationException("ClusterDependencyMustStayDeferred"));
        services.AddScoped<INotificationSender, NoSend>();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        // Act
        INotificationAdministrationService service = scope.ServiceProvider.GetRequiredService<INotificationAdministrationService>();
        ServiceResult result = await service.SendTestMessageAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.NotFound, result.Error!.Type);
        Assert.Equal(1, store.Reads);
    }

    private class Allowed : IAdministratorAccessPolicy
    {
        public ServiceError? GetAccessError()
        {
            return null;
        }
    }

    private class NoSend : INotificationSender
    {
        public Task SendAsync(NotificationRecipient recipient, string message, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("MissingChatMustNotSend");
        }
    }

    private class Store : INotificationSettingsUnitOfWork, ITelegramChatRepository
    {
        public int Reads { get; private set; }
        public ITelegramChatRepository TelegramChats => this;
        public IMonitoredServerRepository MonitoredServers => throw new InvalidOperationException("UnusedRepository");
        public IMonitoredContainerRepository MonitoredContainers => throw new InvalidOperationException("UnusedRepository");
        public INotificationBindingRepository NotificationBindings => throw new InvalidOperationException("UnusedRepository");

        public Task<TelegramChat?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<TelegramChat?>(null);
        }

        public Task<IReadOnlyList<TelegramChat>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("UnexpectedQuery");
        }

        public Task<TelegramChat?> GetByTelegramChatIdAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("UnexpectedQuery");
        }

        public void Create(TelegramChat item)
        {
            throw new InvalidOperationException("UnexpectedWrite");
        }

        public void CreateRange(IEnumerable<TelegramChat> entities)
        {
            throw new InvalidOperationException("UnexpectedWrite");
        }

        public void Delete(TelegramChat item)
        {
            throw new InvalidOperationException("UnexpectedWrite");
        }

        public void DeleteRange(IEnumerable<TelegramChat> entities)
        {
            throw new InvalidOperationException("UnexpectedWrite");
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("UnexpectedWrite");
        }

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("UnexpectedTransaction");
        }
    }
}
