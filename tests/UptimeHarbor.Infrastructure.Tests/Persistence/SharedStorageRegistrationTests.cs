using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class SharedStorageRegistrationTests
{
    [Fact]
    public void StorageScopes_ResolveWithoutStartingWorkersOrOpeningConnection()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = "Server=unused;Database=unused;Integrated Security=true" }).Build();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        // Act
        IProjectionApplyUnitOfWork projection = scope.ServiceProvider.GetRequiredService<IProjectionApplyUnitOfWork>();
        IPendingControlUnitOfWork pending = scope.ServiceProvider.GetRequiredService<IPendingControlUnitOfWork>();
        IObservationCaptureUnitOfWork capture = scope.ServiceProvider.GetRequiredService<IObservationCaptureUnitOfWork>();
        IDeliveryResultUnitOfWork delivery = scope.ServiceProvider.GetRequiredService<IDeliveryResultUnitOfWork>();
        UptimeHarborDbContext context = scope.ServiceProvider.GetRequiredService<UptimeHarborDbContext>();

        // Assert
        Assert.Same(projection, scope.ServiceProvider.GetRequiredService<IProjectionApplyUnitOfWork>());
        Assert.Same(pending.PendingOperations, scope.ServiceProvider.GetRequiredService<IPendingOperationRepository>());
        Assert.Same(capture.Observations, scope.ServiceProvider.GetRequiredService<IObservationCaptureRepository>());
        Assert.Same(delivery.Results, scope.ServiceProvider.GetRequiredService<IDeliveryResultRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IHistoryRetentionQuery>());
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
