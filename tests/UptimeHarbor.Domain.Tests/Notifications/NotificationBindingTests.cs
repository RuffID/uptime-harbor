using UptimeHarbor.Domain.Notifications;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Notifications;

public class NotificationBindingTests
{
    [Fact]
    public void ForServer_CreatesBindingWithOnlyServerTarget()
    {
        Guid serverId = Guid.NewGuid();

        NotificationBinding binding = NotificationBinding.ForServer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            serverId);

        Assert.Equal(serverId, binding.ServerId);
        Assert.Null(binding.ContainerId);
    }

    [Fact]
    public void ForContainer_CreatesBindingWithOnlyContainerTarget()
    {
        Guid containerId = Guid.NewGuid();

        NotificationBinding binding = NotificationBinding.ForContainer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            containerId);

        Assert.Null(binding.ServerId);
        Assert.Equal(containerId, binding.ContainerId);
    }

    [Fact]
    public void DeliveryState_IsStoredSeparatelyForEachBindingAndIncident()
    {
        Guid incidentId = Guid.NewGuid();
        NotificationBinding successful = NotificationBinding.ForServer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        NotificationBinding unsuccessful = NotificationBinding.ForServer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        successful.MarkOutageDelivered(incidentId);

        Assert.False(successful.RequiresOutageDelivery(incidentId));
        Assert.True(successful.RequiresRecoveryDelivery(incidentId));
        Assert.True(unsuccessful.RequiresOutageDelivery(incidentId));
        Assert.False(unsuccessful.RequiresRecoveryDelivery(incidentId));
    }

    [Fact]
    public void RecoveryCannotBeMarkedWithoutSuccessfulOutage()
    {
        NotificationBinding binding = NotificationBinding.ForContainer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            binding.MarkRecoveryDelivered(Guid.NewGuid()));
    }
}
