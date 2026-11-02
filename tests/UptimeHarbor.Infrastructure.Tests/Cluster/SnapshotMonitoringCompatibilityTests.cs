using System.Collections.Immutable;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class SnapshotMonitoringCompatibilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Orders admin")]
    public void Upgrade_PreservesLegacyApplicationMonitoringAndParent(string? name)
    {
        // Arrange
        using UptimeHarborDbContext context = RevisionFixture.CreateContext();
        MonitoredServer server = new(Guid.NewGuid(), "Host", "https://host.invalid/health", null, true);
        MonitoredServer application = MonitoredServer.CreateApplication(Guid.NewGuid(), server.Id,
            "https://app.invalid/health", null, true, name);
        application.RecordSuccessfulCheck("orders", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        application.RecordFailedCheck(DateTimeOffset.UnixEpoch.AddHours(2), 1);
        SharedSnapshotRow serverRow = SnapshotSchema.Capture(context, context.Model.FindEntityType(typeof(MonitoredServer))!, server);
        SharedSnapshotRow applicationRow = SnapshotSchema.Capture(context, context.Model.FindEntityType(typeof(MonitoredServer))!, application);
        SharedSnapshotRow legacyServer = serverRow with { Fields = serverRow.Fields.Remove("Type").Remove("ServerId") };
        SharedSnapshotRow legacyApplication = applicationRow with
        {
            Entity = "UptimeHarbor.Domain.Monitoring.MonitoredContainer",
            Fields = applicationRow.Fields.Remove("Type").Remove("Name").Remove("NormalizedName")
        };
        AcceptedValue accepted = new(application.Id, DeletedEntityKind.Container, 1, new ConfigurationValue { Name = name });
        RevisionImage image = new(Guid.NewGuid(), 1)
        {
            ProjectionRows = [legacyServer, legacyApplication],
            Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(application.Id, accepted)
        };

        // Act
        RevisionImage upgraded = SnapshotMonitoringCompatibility.Upgrade(image);
        SnapshotSchema.Validate(context, upgraded.ProjectionRows);

        // Assert
        Assert.Equal(2, upgraded.ProjectionRows.Length);
        foreach (SharedSnapshotRow expected in new[] { serverRow, applicationRow })
        {
            SharedSnapshotRow actual = Assert.Single(upgraded.ProjectionRows,
                row => row.Fields["Id"].GetGuid() == expected.Fields["Id"].GetGuid());
            Assert.Equal(expected.Entity, actual.Entity);
            foreach (var field in expected.Fields)
            {
                Assert.True(JsonElement.DeepEquals(field.Value, actual.Fields[field.Key]), field.Key);
            }
        }
    }

    [Fact]
    public void Upgrade_RejectsUnknownFieldsInsteadOfDroppingThem()
    {
        // Arrange
        SharedSnapshotRow row = new("UptimeHarbor.Domain.Monitoring.MonitoredContainer",
            ImmutableDictionary<string, JsonElement>.Empty.Add("Unexpected", JsonSerializer.SerializeToElement(true)));
        RevisionImage image = new(Guid.NewGuid(), 1) { ProjectionRows = [row] };

        // Act
        Exception? error = Record.Exception(() => SnapshotMonitoringCompatibility.Upgrade(image));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }
}
