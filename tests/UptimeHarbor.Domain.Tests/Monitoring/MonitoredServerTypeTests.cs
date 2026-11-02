using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Monitoring;

public class MonitoredServerTypeTests
{
    [Fact]
    public void Application_CanBeCreatedWithoutServerAndLinkedLaterWithoutResettingState()
    {
        // Arrange
        MonitoredServer application = new(Guid.NewGuid(), "Orders", "https://orders.example/health",
            null, true, ServerType.Application);
        DateTimeOffset checkedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        application.RecordSuccessfulCheck("orders-process", checkedAt.AddHours(-1), checkedAt);
        Guid serverId = Guid.NewGuid();
        long version = application.Version;

        // Act
        application.AssignServer(serverId);

        // Assert
        Assert.Equal(ServerType.Application, application.Type);
        Assert.Equal(serverId, application.ServerId);
        Assert.Equal("Orders", application.Name);
        Assert.Equal("orders-process", application.LastKnownName);
        Assert.Equal(MonitoringStatus.Available, application.Status);
        Assert.Equal(version + 1, application.Version);

        // Act
        application.AssignServer(null);

        // Assert
        Assert.Null(application.ServerId);
        Assert.Equal(checkedAt.AddHours(-1), application.StartedAtUtc);
        Assert.Equal(MonitoringStatus.Available, application.Status);
    }

    [Fact]
    public void Server_RejectsParentAndUnknownType()
    {
        // Arrange
        Guid id = Guid.NewGuid();
        MonitoredServer server = new(id, "Host", "https://host.example/health", null, true);
        Action assign = () => server.AssignServer(Guid.NewGuid());
        Action unknownType = () => new MonitoredServer(id, "Host", "https://host.example/health",
            null, true, (ServerType)999);

        // Act
        Exception? assignError = Record.Exception(assign);
        Exception? typeError = Record.Exception(unknownType);

        // Assert
        Assert.IsType<ArgumentException>(assignError);
        Assert.IsType<ArgumentOutOfRangeException>(typeError);
        Assert.Null(server.ServerId);
        Assert.Equal(0, server.Version);
    }

    [Fact]
    public void Application_RejectsSelfReferenceBeforeChangingState()
    {
        // Arrange
        MonitoredServer application = new(Guid.NewGuid(), "Orders", "https://orders.example/health",
            null, true, ServerType.Application);

        // Act
        Exception? error = Record.Exception(() => application.AssignServer(application.Id));

        // Assert
        Assert.IsType<ArgumentException>(error);
        Assert.Null(application.ServerId);
        Assert.Equal(0, application.Version);
    }
}
