using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

/// <summary>Только metadata; соединение с SQL Server не открывается.</summary>
public class EnrollmentStorageModelTests
{
    [Fact]
    public void IssuerRegistryIsPublicAndLocalSignerStateIsNeverExported()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        Type[] exported = SnapshotSchema.Entities(context).Select(item => item.ClrType).ToArray();
        IEntityType issuer = context.Model.FindEntityType(typeof(EnrollmentIssuer))!;

        // Assert
        Assert.Contains(typeof(EnrollmentIssuer), exported);
        Assert.DoesNotContain(typeof(LocalEnrollmentIssuerRecord), exported);
        Assert.DoesNotContain(typeof(LocalEnrollmentIssuanceRecord), exported);
        Assert.DoesNotContain(issuer.GetProperties(), property => property.Name.Contains("KeyReference", StringComparison.Ordinal));
        Assert.True(issuer.FindProperty(nameof(EnrollmentIssuer.LogIndex))!.IsConcurrencyToken);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void DurableIssuanceHasUniqueIssueIdentityAndRestrictsIssuerDeletionWithoutPrematureNodeReference()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType issuance = context.Model.FindEntityType(typeof(LocalEnrollmentIssuanceRecord))!;
        IForeignKey issuer = Assert.Single(issuance.GetForeignKeys());

        // Assert
        Assert.Equal([nameof(LocalEnrollmentIssuanceRecord.Id)], issuance.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.Equal(ValueGenerated.Never, issuance.FindProperty(nameof(LocalEnrollmentIssuanceRecord.Id))!.ValueGenerated);
        Assert.Equal(typeof(LocalEnrollmentIssuerRecord), issuer.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, issuer.DeleteBehavior);
        Assert.Contains(issuance.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(item => item.Name).SequenceEqual([nameof(LocalEnrollmentIssuanceRecord.CertificateDigest)]));
        Assert.Equal(65536, issuance.FindProperty(nameof(LocalEnrollmentIssuanceRecord.CertificateDer))!.GetMaxLength());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void IssuerPurposeLifetimeAndGenesisIssuanceAreConstrainedIndependentlyOfLegacyRoots()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IModel model = context.GetService<IDesignTimeModel>().Model;
        IEntityType issuer = model.FindEntityType(typeof(EnrollmentIssuer))!;
        IEntityType issuance = model.FindEntityType(typeof(LocalEnrollmentIssuanceRecord))!;
        ICheckConstraint authorization = Assert.Single(issuance.GetCheckConstraints(),
            item => item.Name == "ck_local_enrollment_issuances_authorization");

        // Assert
        Assert.Contains(issuer.GetCheckConstraints(), item => item.Sql == "[Purpose] = 'UptimeHarbor.EnrollmentPeer.v1'");
        Assert.Contains(issuer.GetCheckConstraints(), item => item.Sql.Contains("DATEADD(year, 3", StringComparison.Ordinal));
        Assert.Contains("[GenesisId] IS NULL AND [AuthorizationIndex] > 0", authorization.Sql);
        Assert.Contains("[GenesisId] IS NOT NULL", authorization.Sql);
        Assert.Contains("[AuthorizationIndex] = 0", authorization.Sql);
        Assert.NotNull(model.FindEntityType(typeof(ClusterTrustRoot)));
        Assert.NotNull(model.FindEntityType(typeof(PeerTrustCredential)));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void GeneratedMigrationOnlyAddsThreeEnrollmentTablesAndMatchesModel()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        IModel snapshot = context.GetService<IModelRuntimeInitializer>().Initialize(migrations.ModelSnapshot!.Model, designTime: true);
        IRelationalModel target = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        KeyValuePair<string, System.Reflection.TypeInfo> entry = Assert.Single(migrations.Migrations,
            item => item.Key.EndsWith("_AddEnrollmentTrust", StringComparison.Ordinal));
        Migration migration = migrations.CreateMigration(entry.Value, context.Database.ProviderName!);

        // Act
        IReadOnlyList<MigrationOperation> operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshot.GetRelationalModel(), target);

        // Assert
        Assert.Empty(operations);
        Assert.Equal(["enrollment_issuers", "local_enrollment_issuances", "local_enrollment_issuers"],
            migration.UpOperations.OfType<CreateTableOperation>().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.All(migration.UpOperations, item => Assert.True(item is CreateTableOperation or CreateIndexOperation));
        Assert.Equal(3, migration.DownOperations.OfType<DropTableOperation>().Count());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static UptimeHarborDbContext CreateContext()
    {
        return new UptimeHarborDbContext(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
    }
}
