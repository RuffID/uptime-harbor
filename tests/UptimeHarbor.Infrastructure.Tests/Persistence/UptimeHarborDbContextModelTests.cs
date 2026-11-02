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
    public void MonitoredServer_StoresNullableBootTimeWithUtcConversion()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IProperty property = context.Model.FindEntityType(typeof(MonitoredServer))!
            .FindProperty(nameof(MonitoredServer.StartedAtUtc))!;

        // Assert
        Assert.True(property.IsNullable);
        Assert.Equal(typeof(DateTimeOffset?), property.ClrType);
        Assert.IsType<UtcDateTimeOffsetConverter>(property.GetTypeMapping().Converter);
    }

    [Fact]
    public void MonitoringEntities_UseExplicitConcurrencyVersion()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IProperty serverVersion = context.Model.FindEntityType(typeof(MonitoredServer))!
            .FindProperty(nameof(MonitoredServer.Version))!;

        // Assert
        Assert.True(serverVersion.IsConcurrencyToken);
    }

    [Fact]
    public void MonitoredServer_ConfiguresRequiredUniqueName()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType serverType = context.Model.FindEntityType(typeof(MonitoredServer))!;
        IProperty name = serverType.FindProperty(nameof(MonitoredServer.Name))!;
        IProperty normalizedName = serverType.FindProperty("NormalizedName")!;
        IIndex index = Assert.Single(
            serverType.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual(["NormalizedName"]));

        // Assert
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
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        Microsoft.EntityFrameworkCore.Metadata.IEntityType server = context.Model.FindEntityType(typeof(MonitoredServer))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType binding = context.Model.FindEntityType(typeof(NotificationBinding))!;

        // Assert
        Assert.NotNull(server.FindProperty(nameof(MonitoredServer.CurrentIncidentId)));
        Assert.NotNull(binding.FindProperty(nameof(NotificationBinding.OutageDeliveredIncidentId)));
        Assert.NotNull(binding.FindProperty(nameof(NotificationBinding.RecoveryDeliveredIncidentId)));
        Assert.Null(server.FindProperty("OutageNotificationSent"));
    }

    [Fact]
    public void Model_ConfiguresSqlServerCompatibleDeleteBehaviors()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredServer))!;
        IEntityType bindingType = context.Model.FindEntityType(typeof(NotificationBinding))!;

        // Assert
        Assert.Equal(DeleteBehavior.NoAction, Assert.Single(containerType.GetForeignKeys()).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(bindingType.GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(TelegramChat)).DeleteBehavior);
        Assert.All(bindingType.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(MonitoredServer)),
            key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
    }

    [Fact]
    public void Model_MapsServersAndApplicationsToOneTableWithStableTypeCodes()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType serverType = context.Model.FindEntityType(typeof(MonitoredServer))!;
        IProperty type = serverType.FindProperty(nameof(MonitoredServer.Type))!;
        var converter = type.GetTypeMapping().Converter!;

        // Assert
        Assert.Null(context.Model.FindEntityType(typeof(MonitoredObject)));
        Assert.Single(context.Model.GetEntityTypes(), entity => typeof(MonitoredObject).IsAssignableFrom(entity.ClrType));
        Assert.Equal("monitored_servers", serverType.GetTableName());
        Assert.False(type.IsNullable);
        Assert.Equal("Server", converter.ConvertToProvider(ServerType.Server));
        Assert.Equal("Application", converter.ConvertToProvider(ServerType.Application));
        Assert.Equal(ServerType.Application, converter.ConvertFromProvider("Application"));
        Assert.Throws<InvalidOperationException>(() => converter.ConvertFromProvider("Unsupported"));
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
    public void Model_ExcludesResourceAssignments()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType? serverPermissions = context.Model.FindEntityType(typeof(ServerVisibilityPermission));
        IEntityType? containerPermissions = context.Model.FindEntityType(typeof(ContainerVisibilityPermission));

        // Assert
        Assert.Null(serverPermissions);
        Assert.Null(containerPermissions);
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
    public void Model_AllowsOptionalParentWithoutResourceAssignments()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType containerType = context.Model.FindEntityType(typeof(MonitoredServer))!;

        // Assert
        Assert.True(containerType.FindProperty(nameof(MonitoredServer.ServerId))!.IsNullable);
        Assert.Single(containerType.GetKeys());
        Assert.Null(context.Model.FindEntityType(typeof(ContainerVisibilityPermission)));
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
