using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class SharedStorageMigrationTests
{
    [Fact]
    public void SnapshotMatchesCurrentSqlServerModel_WithoutOpeningConnection()
    {
        // Arrange
        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        IModelRuntimeInitializer initializer = context.GetService<IModelRuntimeInitializer>();
        IModel snapshot = initializer.Initialize(assembly.ModelSnapshot!.Model, designTime: true);
        IModel current = context.GetService<IDesignTimeModel>().Model;

        // Act
        bool differs = context.GetService<IMigrationsModelDiffer>().HasDifferences(
            snapshot.GetRelationalModel(), current.GetRelationalModel());

        // Assert
        Assert.False(differs);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void NewMigration_DropsOnlyResourceAssignments_AndContainsTheTargetSchema()
    {
        // Arrange
        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        var migrationType = Assert.Single(assembly.Migrations, item => item.Key.EndsWith("_AddSharedClusterStorage", StringComparison.Ordinal));
        Migration migration = assembly.CreateMigration(migrationType.Value, context.Database.ProviderName!);

        // Act
        IReadOnlyList<MigrationOperation> up = migration.UpOperations;
        IReadOnlyList<MigrationOperation> down = migration.DownOperations;

        // Assert
        Assert.Equal(["container_visibility_permissions", "server_visibility_permissions"],
            up.OfType<DropTableOperation>().Select(operation => operation.Name).OrderBy(name => name));
        Assert.Empty(up.OfType<SqlOperation>());
        Assert.Empty(up.OfType<InsertDataOperation>());
        Assert.Contains(up.OfType<CreateTableOperation>(), operation => operation.Name == "operation_decisions");
        Assert.Contains(up.OfType<CreateTableOperation>(), operation => operation.Name == "summary_sources");
        Assert.Contains(up.OfType<CreateTableOperation>(), operation => operation.Name == "recovery_entitlements");
        Assert.Contains(up.OfType<CreateIndexOperation>(), operation => operation.Table == "delivery_intents"
            && operation.IsUnique && operation.Columns.SequenceEqual(["IncidentId", "Kind", "ChatId"]));
        Assert.Contains(down.OfType<CreateTableOperation>(), operation => operation.Name == "server_visibility_permissions");
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
