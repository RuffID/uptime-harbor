using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.Configurations;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class UptimeHarborDbContextModelTests
{
    [Fact]
    public void MonitoringEntities_UseExplicitConcurrencyVersion()
    {
        using UptimeHarborDbContext context = CreateContext();
        IProperty serverVersion = context.Model.FindEntityType(typeof(MonitoredServer))!
            .FindProperty(nameof(MonitoredServer.Version))!;
        IProperty containerVersion = context.Model.FindEntityType(typeof(MonitoredContainer))!
            .FindProperty(nameof(MonitoredContainer.Version))!;

        Assert.True(serverVersion.IsConcurrencyToken);
        Assert.True(containerVersion.IsConcurrencyToken);
    }

    [Fact]
    public void MonitoredServer_ConfiguresRequiredUniqueName()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType serverType = context.Model.FindEntityType(typeof(MonitoredServer))!;
        IProperty name = serverType.FindProperty(nameof(MonitoredServer.Name))!;
        IProperty normalizedName = serverType.FindProperty("NormalizedName")!;
        IIndex index = Assert.Single(
            serverType.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual(["NormalizedName"]));

        Assert.False(name.IsNullable);
        Assert.Equal(ModelConstraints.OBJECT_NAME_MAX_LENGTH, name.GetMaxLength());
        Assert.False(normalizedName.IsNullable);
        Assert.Equal(ModelConstraints.OBJECT_NAME_MAX_LENGTH, normalizedName.GetMaxLength());
        Assert.True(index.IsUnique);
        Assert.Equal(MonitoredServerConfiguration.NAME_UNIQUE_INDEX, index.GetDatabaseName());
    }

    [Fact]
    public void NotificationOutbox_IsPersistedWithoutForeignKeyToMonitoringObject()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType outboxType = context.Model.FindEntityType(typeof(NotificationOutboxJob))!;

        Assert.Equal("notification_outbox_jobs", outboxType.GetTableName());
        Assert.Empty(outboxType.GetForeignKeys());
        Assert.Equal("nvarchar(max)", outboxType.FindProperty(nameof(NotificationOutboxJob.PayloadJson))!.GetColumnType());
    }

    [Fact]
    public void NotificationDeliveryState_IsPersistedPerBindingAndCurrentIncident()
    {
        using UptimeHarborDbContext context = CreateContext();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType server = context.Model.FindEntityType(typeof(MonitoredServer))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType binding = context.Model.FindEntityType(typeof(NotificationBinding))!;

        Assert.NotNull(server.FindProperty(nameof(MonitoredServer.CurrentIncidentId)));
        Assert.NotNull(binding.FindProperty(nameof(NotificationBinding.OutageDeliveredIncidentId)));
        Assert.NotNull(binding.FindProperty(nameof(NotificationBinding.RecoveryDeliveredIncidentId)));
        Assert.Null(server.FindProperty("OutageNotificationSent"));
    }

    [Fact]
    public void Model_ConfiguresSqlServerCompatibleDeleteBehaviors()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredContainer))!;
        IEntityType serverPermissionType = context.Model.FindEntityType(typeof(ServerVisibilityPermission))!;
        IEntityType containerPermissionType = context.Model.FindEntityType(typeof(ContainerVisibilityPermission))!;
        IEntityType notificationBindingType = context.Model.FindEntityType(typeof(NotificationBinding))!;

        IForeignKey containerServerForeignKey = Assert.Single(
            containerType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredServer));
        IForeignKey containerPermissionServerPermissionForeignKey = Assert.Single(
            containerPermissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ServerVisibilityPermission));
        IForeignKey serverPermissionServerForeignKey = Assert.Single(
            serverPermissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredServer));
        IForeignKey containerPermissionContainerForeignKey = Assert.Single(
            containerPermissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredContainer));
        IForeignKey bindingServerForeignKey = Assert.Single(
            notificationBindingType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredServer));
        IForeignKey bindingContainerForeignKey = Assert.Single(
            notificationBindingType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredContainer));

        Assert.Equal(DeleteBehavior.NoAction, containerServerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, containerPermissionServerPermissionForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, serverPermissionServerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, containerPermissionContainerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, bindingServerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, bindingContainerForeignKey.DeleteBehavior);
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
        Assert.Contains(
            uniqueIndexes,
            index => index.GetDatabaseName() == NotificationBindingConfiguration.CHAT_SERVER_UNIQUE_INDEX);
        Assert.Contains(
            uniqueIndexes,
            index => index.GetDatabaseName() == NotificationBindingConfiguration.CHAT_CONTAINER_UNIQUE_INDEX);
    }

    [Fact]
    public void Model_ConfiguresKnownTelegramChatUniqueIndex()
    {
        using UptimeHarborDbContext context = CreateContext();
        IEntityType chatType = context.Model.FindEntityType(typeof(TelegramChat))!;
        IIndex index = Assert.Single(chatType.GetIndexes(), candidate => candidate.IsUnique);

        Assert.Equal(TelegramChatConfiguration.TELEGRAM_CHAT_ID_UNIQUE_INDEX, index.GetDatabaseName());
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
    public void Model_StoresIdentityAndDataProtectionKeysInDedicatedTables()
    {
        using UptimeHarborDbContext context = CreateContext();

        Assert.Equal("users", context.Model.FindEntityType(typeof(UptimeHarbor.Infrastructure.Identity.ApplicationUser))!.GetTableName());
        Assert.Equal("roles", context.Model.FindEntityType(typeof(UptimeHarbor.Infrastructure.Identity.ApplicationRole))!.GetTableName());
        Assert.Equal("user_roles", context.Model.FindEntityType(typeof(IdentityUserRole<Guid>))!.GetTableName());
        Assert.Equal("data_protection_keys", context.Model.FindEntityType(typeof(DataProtectionKey))!.GetTableName());
    }

    [Fact]
    public void Model_AllowsOptionalParentAndKeepsContainerPermissionForeignKeys()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType permissionType = context.Model.FindEntityType(typeof(ContainerVisibilityPermission))!;
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredContainer))!;

        IForeignKey serverPermissionForeignKey = Assert.Single(
            permissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ServerVisibilityPermission));
        IForeignKey containerForeignKey = Assert.Single(
            permissionType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(MonitoredContainer));

        // Assert
        Assert.True(containerType.FindProperty(nameof(MonitoredContainer.ServerId))!.IsNullable);
        Assert.Single(containerType.GetKeys());
        Assert.Equal(
            [nameof(ContainerVisibilityPermission.UserId), nameof(ContainerVisibilityPermission.ServerId)],
            serverPermissionForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(ContainerVisibilityPermission.ContainerId)],
            containerForeignKey.Properties.Select(property => property.Name));
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
                .UseSqlServer(
                    "Server=localhost;Database=uptime_harbor_model_tests;" +
                    "User Id=test;Password=test;TrustServerCertificate=True")
                .Options;

        return new UptimeHarborDbContext(options);
    }
}
