using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.ObjectAdministration;

public class ObjectAdministrationServiceTests
{
    [Fact]
    public async Task CreateServerAsync_AssignsGuidAndUnknownStatus()
    {
        TestData data = new();

        ServiceResult<AdminServerDto> result = await data.Service.CreateServerAsync(
            "https://server.example/health",
            "Основной сервер",
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.Data!.Id);
        Assert.Equal(MonitoringStatus.Unknown, result.Data.Status);
        Assert.Single(data.Servers.Items);
        Assert.Equal(1, data.Publisher.PublishCount);
    }

    [Fact]
    public async Task CreateContainerAsync_RequiresExistingServer()
    {
        TestData data = new();

        ServiceResult<AdminContainerDto> result = await data.Service.CreateContainerAsync(
            Guid.NewGuid(),
            "https://container.example/health",
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Validation, result.Error!.Type);
        Assert.Equal("serverId", result.Error.Field);
        Assert.Empty(data.Containers.Items);
    }

    [Fact]
    public async Task CreateContainerAsync_ForExistingServer_AssignsGuidAndUnknownStatus()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);

        ServiceResult<AdminContainerDto> result = await data.Service.CreateContainerAsync(
            server.Id,
            "https://container.example/health",
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.Data!.Id);
        Assert.Equal(server.Id, result.Data.ServerId);
        Assert.Equal(MonitoringStatus.Unknown, result.Data.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/health")]
    [InlineData("http://")]
    [InlineData("ftp://example.com/health")]
    [InlineData("mailto:owner@example.com")]
    public async Task CreateServerAsync_WhenEndpointIsInvalid_ReturnsFieldValidationError(
        string endpointUrl)
    {
        TestData data = new();

        ServiceResult<AdminServerDto> result = await data.Service.CreateServerAsync(
            endpointUrl,
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Validation, result.Error!.Type);
        Assert.Equal("endpointUrl", result.Error.Field);
        Assert.Empty(data.Servers.Items);
    }

    [Fact]
    public async Task CreateServerAsync_EnforcesEndpointMaximumLength()
    {
        TestData data = new();
        const string prefix = "https://example.com/";
        string maximumEndpoint = prefix + new string('a', ModelConstraints.ENDPOINT_URL_MAX_LENGTH - prefix.Length);
        string excessiveEndpoint = maximumEndpoint + "a";

        ServiceResult<AdminServerDto> maximumResult = await data.Service.CreateServerAsync(
            maximumEndpoint,
            null,
            true,
            TestContext.Current.CancellationToken);
        ServiceResult<AdminServerDto> excessiveResult = await data.Service.CreateServerAsync(
            excessiveEndpoint,
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(maximumResult.Success);
        Assert.False(excessiveResult.Success);
        Assert.Equal("endpointUrl", excessiveResult.Error!.Field);
    }

    [Fact]
    public async Task CreateServerAsync_EnforcesDescriptionMaximumLength()
    {
        TestData data = new();
        string maximumDescription = new('a', ModelConstraints.DESCRIPTION_MAX_LENGTH);
        string excessiveDescription = maximumDescription + "a";

        ServiceResult<AdminServerDto> maximumResult = await data.Service.CreateServerAsync(
            "https://server.example/health",
            maximumDescription,
            true,
            TestContext.Current.CancellationToken);
        ServiceResult<AdminServerDto> excessiveResult = await data.Service.CreateServerAsync(
            "https://second-server.example/health",
            excessiveDescription,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(maximumResult.Success);
        Assert.False(excessiveResult.Success);
        Assert.Equal("description", excessiveResult.Error!.Field);
    }

    [Fact]
    public async Task UpdateServerAsync_ChangesSettingsAndClearsEndpointSpecificState()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        server.RecordSuccessfulCheck(
            "old-server-name",
            new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero));

        ServiceResult<AdminServerDto> result = await data.Service.UpdateServerAsync(
            server.Id,
            "https://new-server.example/health",
            "Новое описание",
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Data!.LastKnownName);
        Assert.Equal(MonitoringStatus.Unknown, result.Data.Status);
        Assert.Equal("Новое описание", result.Data.Description);
    }

    [Fact]
    public async Task UpdateContainerAsync_ChangesSettingsAndClearsEndpointSpecificState()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        container.RecordSuccessfulCheck("old-container-name", checkedAtUtc.AddHours(-1), checkedAtUtc);

        ServiceResult<AdminContainerDto> result = await data.Service.UpdateContainerAsync(
            container.Id,
            "https://new-container.example/health",
            "Новое описание",
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Data!.LastKnownName);
        Assert.Null(container.StartedAtUtc);
        Assert.Equal(MonitoringStatus.Unknown, result.Data.Status);
    }

    [Fact]
    public async Task UpdateServerAsync_DisablingServerDoesNotDisableContainer()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);

        await data.Service.UpdateServerAsync(
            server.Id,
            server.EndpointUrl,
            server.Description,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(server.IsMonitoringEnabled);
        Assert.True(container.IsMonitoringEnabled);
    }

    [Fact]
    public async Task UpdateContainerAsync_DisablingContainerDoesNotDisableServer()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);

        await data.Service.UpdateContainerAsync(
            container.Id,
            container.EndpointUrl,
            container.Description,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(container.IsMonitoringEnabled);
        Assert.True(server.IsMonitoringEnabled);
    }

    [Fact]
    public async Task UpdateServerAsync_DisablingMonitoringResetsIncidentWithoutRecovery()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        server.RecordFailedCheck(
            new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            failuresBeforeUnavailable: 1);
        server.MarkOutageNotificationSent();

        ServiceResult<AdminServerDto> result = await data.Service.UpdateServerAsync(
            server.Id,
            server.EndpointUrl,
            server.Description,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
        Assert.False(server.OutageNotificationSent);
    }

    [Fact]
    public async Task DeleteContainerAsync_RemovesContainerAndPublishesChange()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);

        ServiceResult result = await data.Service.DeleteContainerAsync(
            container.Id,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(data.Containers.Items);
        Assert.Equal(1, data.Publisher.PublishCount);
    }

    [Fact]
    public async Task DeleteServerAsync_WhenConfirmedCountMatches_RemovesServer()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        data.AddContainer(server, isMonitoringEnabled: true);

        ServiceResult result = await data.Service.DeleteServerAsync(
            server.Id,
            expectedContainerCount: 1,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(data.Servers.Items);
        Assert.Equal(1, data.Publisher.PublishCount);
    }

    [Fact]
    public async Task DeleteServerAsync_WhenContainerCountChanged_ReturnsConflict()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        data.AddContainer(server, isMonitoringEnabled: true);

        ServiceResult result = await data.Service.DeleteServerAsync(
            server.Id,
            expectedContainerCount: 0,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Conflict, result.Error!.Type);
        Assert.Single(data.Servers.Items);
        Assert.Equal(0, data.Publisher.PublishCount);
    }

    [Theory]
    [InlineData("GetServers")]
    [InlineData("GetContainers")]
    [InlineData("CreateServer")]
    [InlineData("UpdateServer")]
    [InlineData("CreateContainer")]
    [InlineData("UpdateContainer")]
    [InlineData("GetDeletionImpact")]
    [InlineData("DeleteServer")]
    [InlineData("DeleteContainer")]
    public async Task AdministrativeUseCase_ForOrdinaryUser_ReturnsForbidden(string operation)
    {
        TestData data = new(isAdministrator: false);
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);

        ServiceError error = operation switch
        {
            "GetServers" => (await data.Service.GetServersAsync(TestContext.Current.CancellationToken)).Error!,
            "GetContainers" => (await data.Service.GetContainersAsync(server.Id, TestContext.Current.CancellationToken)).Error!,
            "CreateServer" => (await data.Service.CreateServerAsync("https://new.example/health", null, true, TestContext.Current.CancellationToken)).Error!,
            "UpdateServer" => (await data.Service.UpdateServerAsync(server.Id, server.EndpointUrl, null, true, TestContext.Current.CancellationToken)).Error!,
            "CreateContainer" => (await data.Service.CreateContainerAsync(server.Id, "https://new-container.example/health", null, true, TestContext.Current.CancellationToken)).Error!,
            "UpdateContainer" => (await data.Service.UpdateContainerAsync(container.Id, container.EndpointUrl, null, true, TestContext.Current.CancellationToken)).Error!,
            "GetDeletionImpact" => (await data.Service.GetServerDeletionImpactAsync(server.Id, TestContext.Current.CancellationToken)).Error!,
            "DeleteServer" => (await data.Service.DeleteServerAsync(server.Id, 1, TestContext.Current.CancellationToken)).Error!,
            "DeleteContainer" => (await data.Service.DeleteContainerAsync(container.Id, TestContext.Current.CancellationToken)).Error!,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Неизвестная операция.")
        };

        Assert.Equal(ServiceErrorType.Forbidden, error.Type);
        Assert.Equal(0, data.UnitOfWork.SaveCount);
        Assert.Equal(0, data.Publisher.PublishCount);
    }

    private class TestData
    {
        public TestData(bool isAdministrator = true)
        {
            Servers = new FakeServerRepository();
            Containers = new FakeContainerRepository();
            UnitOfWork = new FakeObjectAdministrationUnitOfWork(Servers, Containers);
            Publisher = new FakeChangePublisher();
            Service = new ObjectAdministrationService(
                new FakeAccessContext(isAdministrator),
                UnitOfWork,
                Publisher);
        }

        public FakeServerRepository Servers { get; }

        public FakeContainerRepository Containers { get; }

        public FakeObjectAdministrationUnitOfWork UnitOfWork { get; }

        public FakeChangePublisher Publisher { get; }

        public ObjectAdministrationService Service { get; }

        public MonitoredServer AddServer(bool isMonitoringEnabled)
        {
            MonitoredServer server = new(
                Guid.NewGuid(),
                $"https://server-{Servers.Items.Count}.example/health",
                null,
                isMonitoringEnabled);
            Servers.Items.Add(server);
            return server;
        }

        public MonitoredContainer AddContainer(MonitoredServer server, bool isMonitoringEnabled)
        {
            MonitoredContainer container = new(
                Guid.NewGuid(),
                server.Id,
                $"https://container-{Containers.Items.Count}.example/health",
                null,
                isMonitoringEnabled);
            Containers.Items.Add(container);
            return container;
        }
    }

    private class FakeAccessContext(bool isAdministrator) : IUserAccessContext
    {
        public bool IsAuthenticated => true;

        public bool IsAdministrator { get; } = isAdministrator;

        public bool IsEnabled => true;

        public Guid UserId { get; } = Guid.NewGuid();
    }

    private class FakeObjectAdministrationUnitOfWork(
        IMonitoredServerRepository servers,
        IMonitoredContainerRepository containers)
        : IObjectAdministrationUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = servers;

        public IMonitoredContainerRepository MonitoredContainers { get; } = containers;

        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    private class FakeServerRepository : IMonitoredServerRepository
    {
        public List<MonitoredServer> Items { get; } = [];

        public Task<List<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.ToList());

        public Task<MonitoredServer?> GetItemByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

        public void Create(MonitoredServer item) => Items.Add(item);

        public void CreateRange(IEnumerable<MonitoredServer> entities) => Items.AddRange(entities);

        public void Delete(MonitoredServer item) => Items.Remove(item);

        public void DeleteRange(IEnumerable<MonitoredServer> entities)
        {
            foreach (MonitoredServer entity in entities.ToList())
            {
                Items.Remove(entity);
            }
        }
    }

    private class FakeContainerRepository : IMonitoredContainerRepository
    {
        public List<MonitoredContainer> Items { get; } = [];

        public Task<List<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.ToList());

        public Task<List<MonitoredContainer>> GetByServerIdAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Where(item => item.ServerId == serverId).ToList());

        public Task<MonitoredContainer?> GetItemByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

        public void Create(MonitoredContainer item) => Items.Add(item);

        public void CreateRange(IEnumerable<MonitoredContainer> entities) => Items.AddRange(entities);

        public void Delete(MonitoredContainer item) => Items.Remove(item);

        public void DeleteRange(IEnumerable<MonitoredContainer> entities)
        {
            foreach (MonitoredContainer entity in entities.ToList())
            {
                Items.Remove(entity);
            }
        }
    }

    private class FakeChangePublisher : IObjectAdministrationChangePublisher
    {
        public int PublishCount { get; private set; }

        public Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default)
        {
            PublishCount++;
            return Task.CompletedTask;
        }
    }
}
