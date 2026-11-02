using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class UnifiedMonitoringMigrationTests
{
    [Fact]
    public void Up_CopiesEveryLegacyApplicationFieldBeforeDroppingItsTable()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();
        Migration migration = FindMigration(context, "_UnifyMonitoredServersAndApplications");
        Migration previous = FindMigration(context, "_AddMonitoredServerStartedAtUtc");
        IEntityType legacy = previous.TargetModel.FindEntityType("UptimeHarbor.Domain.Monitoring.MonitoredContainer")!;
        string[] expectedColumns = legacy.GetProperties().Select(property => property.GetColumnName())
            .Concat(["Name", "NormalizedName", "Type"]).OrderBy(name => name).ToArray();

        // Act
        List<MigrationOperation> operations = migration.UpOperations.ToList();
        SqlOperation transfer = Assert.Single(operations.OfType<SqlOperation>(),
            operation => operation.Sql.Contains("INSERT INTO [monitored_servers]", StringComparison.Ordinal));
        Match columns = Regex.Match(transfer.Sql, @"INSERT INTO \[monitored_servers\]\s*\((?<columns>.*?)\)\s*SELECT",
            RegexOptions.Singleline);
        string[] copiedColumns = Regex.Matches(columns.Groups["columns"].Value, @"\[(\w+)\]")
            .Select(match => match.Groups[1].Value).OrderBy(name => name).ToArray();

        // Assert
        Assert.True(columns.Success);
        Assert.Equal(expectedColumns, copiedColumns);
        DropTableOperation drop = Assert.Single(operations.OfType<DropTableOperation>());
        Assert.Equal("monitored_containers", drop.Name);
        Assert.True(operations.IndexOf(transfer) < operations.IndexOf(drop));
        Assert.Contains("MonitoringTargetIdCollision", Assert.IsType<SqlOperation>(operations[0]).Sql, StringComparison.Ordinal);
        Assert.All(operations.OfType<AddForeignKeyOperation>(), operation =>
        {
            Assert.Equal(ReferentialAction.NoAction, operation.OnDelete);
            Assert.True(operations.IndexOf(operation) > operations.IndexOf(transfer));
        });
        AddColumnOperation type = Assert.Single(operations.OfType<AddColumnOperation>(), operation => operation.Name == "Type");
        Assert.True(type.IsNullable);
        Assert.Null(type.DefaultValue);
        Assert.Contains(operations.OfType<AlterColumnOperation>(), operation => operation.Name == "Type" && !operation.IsNullable);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Down_PreservesApplicationsAndRejectsUnrepresentableNamesBeforeChangingSchema()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();
        Migration migration = FindMigration(context, "_UnifyMonitoredServersAndApplications");

        // Act
        List<MigrationOperation> operations = migration.DownOperations.ToList();
        SqlOperation guard = Assert.IsType<SqlOperation>(operations[0]);
        SqlOperation transfer = Assert.Single(operations.OfType<SqlOperation>(),
            operation => operation.Sql.Contains("INSERT INTO [monitored_containers]", StringComparison.Ordinal));

        // Assert
        Assert.Contains("ApplicationNamesCannotBeDowngraded", guard.Sql, StringComparison.Ordinal);
        Assert.Contains("UnsupportedMonitoringTargetType", guard.Sql, StringComparison.Ordinal);
        Assert.True(operations.IndexOf(Assert.Single(operations.OfType<CreateTableOperation>())) < operations.IndexOf(transfer));
        Assert.All(operations.OfType<DropColumnOperation>(), operation =>
            Assert.True(operations.IndexOf(operation) > operations.IndexOf(transfer)));
        Assert.True(transfer.Sql.IndexOf("INSERT INTO", StringComparison.Ordinal)
            < transfer.Sql.IndexOf("DELETE FROM", StringComparison.Ordinal));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static UptimeHarborDbContext CreateContext()
    {
        return new UptimeHarborDbContext(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
    }

    private static Migration FindMigration(UptimeHarborDbContext context, string suffix)
    {
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        KeyValuePair<string, TypeInfo> entry = Assert.Single(assembly.Migrations,
            item => item.Key.EndsWith(suffix, StringComparison.Ordinal));

        return assembly.CreateMigration(entry.Value, context.Database.ProviderName!);
    }
}
