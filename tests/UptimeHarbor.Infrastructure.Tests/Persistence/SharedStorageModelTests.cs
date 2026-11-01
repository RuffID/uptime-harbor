using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;
using UptimeHarbor.Application.Abstractions.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Infrastructure;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class SharedStorageModelTests
{
    [Fact]
    public void Model_ExcludesLegacyPermissions_WithoutMappingThenIgnoringThem()
    {
        // Arrange
        DbContextOptions<UptimeHarborDbContext> options = new DbContextOptionsBuilder<UptimeHarborDbContext>()
            .UseSqlServer()
            .ConfigureWarnings(warnings => warnings.Throw(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
        using UptimeHarborDbContext context = new(options);

        // Act
        IModel model = context.Model;

        // Assert
        Assert.Null(model.FindEntityType(typeof(ServerVisibilityPermission)));
        Assert.Null(model.FindEntityType(typeof(ContainerVisibilityPermission)));
        Assert.NotNull(model.FindEntityType(typeof(ApplicationUser)));
    }

    [Fact]
    public void Model_ContainsDurableSharedAndLocalRecords_WithUniqueOperationAndLoginKeys()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IModel model = context.Model;

        // Assert
        Assert.NotNull(model.FindEntityType(typeof(OperationDecision)));
        Assert.NotNull(model.FindEntityType(typeof(OperationControl)));
        Assert.NotNull(model.FindEntityType(typeof(PendingOperation)));
        Assert.NotNull(model.FindEntityType(typeof(ConflictRecord)));
        Assert.NotNull(model.FindEntityType(typeof(Tombstone)));
        Assert.NotNull(model.FindEntityType(typeof(LocalNodeState)));
        Assert.NotNull(model.FindEntityType(typeof(RecoveryMarker)));
        Assert.NotNull(model.FindEntityType(typeof(ProjectionCursor)));
        Assert.NotNull(model.FindEntityType(typeof(ObservationCaptureCursor)));
        Assert.NotNull(model.FindEntityType(typeof(CapturedObservation)));
        Assert.NotNull(model.FindEntityType(typeof(CapturedDeliveryResult)));
        IEntityType decision = model.FindEntityType(typeof(OperationDecision))!;
        Assert.Equal(["Id"], decision.FindPrimaryKey()!.Properties.Select(property => property.Name));
        IEntityType user = model.FindEntityType(typeof(ApplicationUser))!;
        IIndex login = Assert.Single(user.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ApplicationUser.NormalizedUserName)]));
        Assert.True(login.IsUnique);
        Assert.Null(login.GetFilter());
        Assert.False(user.FindProperty(nameof(ApplicationUser.NormalizedUserName))!.IsNullable);
        Assert.True(user.FindProperty(nameof(ApplicationUser.UserRevision))!.IsConcurrencyToken);
        Assert.False(context.Database.GetDbConnection().State == System.Data.ConnectionState.Open);
    }

    [Fact]
    public void ObserversAndHistory_HaveStreamKeysAndNonCascadingReferences()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IEntityType result = context.Model.FindEntityType(typeof(ObserverResult))!;
        IEntityType source = context.Model.FindEntityType(typeof(SummarySource))!;
        IEntityType entitlement = context.Model.FindEntityType(typeof(RecoveryEntitlement))!;

        // Assert
        Assert.Contains(result.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual(["TargetId", "ObserverNodeId", "StorageGeneration", "CheckGeneration", "Sequence"]));
        Assert.NotNull(result.FindProperty(nameof(ObserverResult.EndpointName)));
        Assert.NotNull(result.FindProperty(nameof(ObserverResult.StartedAtUtc)));
        Assert.Equal(3, source.GetForeignKeys().Count());
        Assert.All(source.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        Assert.DoesNotContain(entitlement.GetForeignKeys(), key => key.PrincipalEntityType.GetTableName() == "notification_bindings");
        Assert.Contains(entitlement.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual(["IncidentId", "ChatId"]));
    }

    [Fact]
    public void DesignModel_RestrictsRolesAndDeliveryBudget_WithoutSqlConsensusState()
    {
        // Arrange
        using UptimeHarborDbContext context = CreateContext();

        // Act
        IModel model = context.GetService<IDesignTimeModel>().Model;

        // Assert
        IEntityType role = model.FindEntityType(typeof(ApplicationRole))!;
        Assert.Contains(role.GetCheckConstraints(), constraint => constraint.Name == "ck_roles_administrator"
            && constraint.Sql.Contains("ADMINISTRATOR", StringComparison.Ordinal));
        IEntityType intent = model.FindEntityType(typeof(DeliveryIntent))!;
        Assert.Contains(intent.GetCheckConstraints(), constraint => constraint.Sql.Contains("BETWEEN 0 AND 5", StringComparison.Ordinal));
        Assert.Contains(intent.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(DeliveryIntent.LogicalKey)]));
        IEntityType local = model.FindEntityType(typeof(LocalNodeState))!;
        Assert.Null(local.FindProperty("VotedFor"));
        Assert.Null(local.FindProperty("Term"));
        Assert.NotNull(model.FindEntityType(typeof(IdentityUserRole<Guid>)));
        Assert.Null(model.FindEntityType(typeof(AppliedJournalReceipt))!.FindProperty("Payload"));
    }

    [Fact]
    public void SharedAccountMapping_PreservesLocalLockoutAndAcceptsOnlyNewRevision()
    {
        // Arrange
        ApplicationUser user = new(Guid.NewGuid(), "old") { AccessFailedCount = 3, LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(1) };
        DateTimeOffset? lockoutEnd = user.LockoutEnd;
        SharedAccount account = new(user.Id, Guid.NewGuid(), "Ａｄｍｉｎ",
            new ProtectedCredential("hash", 1, "security", "concurrency"), true, 7);

        // Act
        user.ApplySharedAccount(account);

        // Assert
        Assert.Equal("ADMIN", user.NormalizedUserName);
        Assert.Equal(7, user.UserRevision);
        Assert.Equal(3, user.AccessFailedCount);
        Assert.Equal(lockoutEnd, user.LockoutEnd);
        Assert.Throws<InvalidOperationException>(() => user.ApplySharedAccount(account));
    }

    [Fact]
    public async Task Projection_RejectsTombstonedMember_FromStaleTrackedCopy()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = "Server=unused;Database=unused;Integrated Security=true"
            }).Build();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        UptimeHarborDbContext context = scope.ServiceProvider.GetRequiredService<UptimeHarborDbContext>();
        JournalPosition position = new(new ClusterIdentity(Guid.NewGuid(), 1), 8, 1);
        ClusterMember member = new(Guid.NewGuid(), position, Guid.NewGuid(),
            "https://peer.example.test/", "peer", 1, true);
        // Имитируем старую копию рядом с сохранённым tombstone; FindAsync берёт данные из tracker.
        context.Attach(member);
        context.Add(new Tombstone(member.Id,
            new JournalPosition(position.Cluster, 7, 1), DeletedEntityKind.Node,
            new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero)));
        IProjectionRepository repository = scope.ServiceProvider.GetRequiredService<IProjectionRepository>();
        ProjectionWriteSet changes = new()
        {
            NodeId = Guid.NewGuid(),
            ExpectedCursorIndex = 7,
            Receipt = new AppliedJournalReceipt(Guid.NewGuid(), position, new string('A', 64)),
            Records = [member]
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(() =>
            repository.StageAsync(changes, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("A tombstoned identity cannot be recreated.", failure.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static UptimeHarborDbContext CreateContext()
    {
        return new UptimeHarborDbContext(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
    }
}
