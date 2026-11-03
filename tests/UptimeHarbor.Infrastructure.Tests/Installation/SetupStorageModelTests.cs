using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupStorageModelTests
{
    [Fact]
    public void MigrationOnlyAddsFourLocalTablesAndSnapshotNeverExportsThem()
    {
        // Arrange
        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> entry = Assert.Single(migrations.Migrations,
            item => item.Key.EndsWith("_AddLocalInstallationSetup", StringComparison.Ordinal));
        Migration migration = migrations.CreateMigration(entry.Value, context.Database.ProviderName!);
        IModel snapshot = context.GetService<IModelRuntimeInitializer>().Initialize(migrations.ModelSnapshot!.Model, designTime: true);

        // Act
        bool different = context.GetService<IMigrationsModelDiffer>().HasDifferences(snapshot.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Type[] exported = SnapshotSchema.Entities(context).Select(item => item.ClrType).ToArray();

        // Assert
        Assert.False(different);
        Assert.Equal(4, migration.UpOperations.OfType<CreateTableOperation>().Count());
        Assert.All(migration.UpOperations, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation));
        Assert.Equal(4, migration.DownOperations.OfType<DropTableOperation>().Count());
        Assert.DoesNotContain(typeof(LocalSetupAccount), exported);
        Assert.DoesNotContain(typeof(LocalProfileRecord), exported);
        Assert.DoesNotContain(typeof(LocalAttemptRecord), exported);
        Assert.DoesNotContain(typeof(LocalStepRecord), exported);
        Assert.DoesNotContain(typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey), exported);
        Assert.True(context.Model.FindEntityType(typeof(LocalProfileRecord))!.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.True(context.Model.FindEntityType(typeof(LocalAttemptRecord))!.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.True(context.Model.FindEntityType(typeof(LocalSetupAccount))!.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
