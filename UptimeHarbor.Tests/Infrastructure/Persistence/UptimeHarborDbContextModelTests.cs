using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class UptimeHarborDbContextModelTests
{
    [Fact]
    public void Model_ConfiguresRequiredCascadeRelationships()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredContainer))!;
        IEntityType serverPermissionType = context.Model.FindEntityType(typeof(ServerVisibilityPermission))!;
        IEntityType containerPermissionType = context.Model.FindEntityType(typeof(ContainerVisibilityPermission))!;

        IForeignKey containerServerForeignKey = Assert.Single(
            containerType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredServer));
        IForeignKey containerPermissionServerPermissionForeignKey = Assert.Single(
            containerPermissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ServerVisibilityPermission));
        IForeignKey serverPermissionServerForeignKey = Assert.Single(
            serverPermissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredServer));

        Assert.Equal(DeleteBehavior.Cascade, containerServerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, containerPermissionServerPermissionForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, serverPermissionServerForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Model_MapsConcreteMonitoringTypesToSeparateTables()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType serverType = context.Model.FindEntityType(typeof(MonitoredServer))!;
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredContainer))!;

        Assert.Null(context.Model.FindEntityType(typeof(MonitoredObject)));
        Assert.Equal("monitored_servers", serverType.GetTableName());
        Assert.Equal("monitored_containers", containerType.GetTableName());
    }

    [Fact]
    public void Model_ConfiguresUniqueNotificationBindingsForEachTarget()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType bindingType = context.Model.FindEntityType(typeof(NotificationBinding))!;
        List<IIndex> uniqueIndexes = bindingType.GetIndexes()
            .Where(index => index.IsUnique)
            .ToList();

        Assert.Contains(
            uniqueIndexes,
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(NotificationBinding.TelegramChatId), nameof(NotificationBinding.ServerId)]));
        Assert.Contains(
            uniqueIndexes,
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(NotificationBinding.TelegramChatId), nameof(NotificationBinding.ContainerId)]));
    }

    [Fact]
    public void Model_UsesCompositeKeysForVisibilityPermissions()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType serverPermissionType = context.Model.FindEntityType(typeof(ServerVisibilityPermission))!;
        IEntityType containerPermissionType = context.Model.FindEntityType(typeof(ContainerVisibilityPermission))!;

        Assert.Equal(
            [nameof(ServerVisibilityPermission.UserId), nameof(ServerVisibilityPermission.ServerId)],
            serverPermissionType.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(ContainerVisibilityPermission.UserId), nameof(ContainerVisibilityPermission.ContainerId)],
            containerPermissionType.FindPrimaryKey()!.Properties.Select(property => property.Name));
    }

    [Fact]
    public void UtcConverter_NormalizesStoredValueAndRestoresUtcOffset()
    {
        UtcDateTimeOffsetConverter converter = new();
        DateTimeOffset localValue = new(2026, 8, 25, 17, 0, 0, TimeSpan.FromHours(7));

        DateTime storedValue = (DateTime)converter.ConvertToProvider(localValue)!;
        DateTimeOffset restoredValue = (DateTimeOffset)converter.ConvertFromProvider(storedValue)!;

        Assert.Equal(DateTimeKind.Utc, storedValue.Kind);
        Assert.Equal(TimeSpan.Zero, restoredValue.Offset);
        Assert.Equal(localValue.UtcDateTime, restoredValue.UtcDateTime);
    }

    private static UptimeHarborDbContext CreateContext()
    {
        DbContextOptions<UptimeHarborDbContext> options =
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseNpgsql("Host=localhost;Database=uptime_harbor_model_tests;Username=test;Password=test")
                .Options;

        return new UptimeHarborDbContext(options);
    }
}
