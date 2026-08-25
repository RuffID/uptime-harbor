using UptimeHarbor.Domain.Notifications;

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
}
