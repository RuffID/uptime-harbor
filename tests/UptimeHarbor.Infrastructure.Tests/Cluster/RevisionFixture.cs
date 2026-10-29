using System.Collections.Immutable;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class RevisionFixture
{
    public Guid Node { get; } = Guid.NewGuid();
    public Guid Actor { get; } = Guid.NewGuid();
    public Guid Role { get; } = Guid.NewGuid();
    public RevisionImage Baseline { get; }

    public RevisionFixture()
    {
        ConfigurationValue credential = new()
        {
            Name = "admin", Enabled = true, RoleId = Role, PasswordHash = "protected-hash",
            SecurityStamp = "security", ConcurrencyStamp = "concurrency"
        };
        AcceptedValue account = new(Actor, DeletedEntityKind.Account, 1, credential);
        Baseline = new RevisionImage(Guid.NewGuid(), 1)
        {
            Index = 2, Term = 1, Digest = new string('A', 64),
            Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(Actor, account),
            Versions = ImmutableDictionary<string, AcceptedValue>.Empty.Add(RevisionReducer.VersionKey(Actor, 1), account)
        };
    }

    public RevisionCommand Server()
    {
        return new RevisionCommand
        {
            ClusterId = Baseline.ClusterId, ClusterGeneration = 1, OperationId = Guid.NewGuid(), OriginNodeId = Node,
            ActorUserId = Actor, ActorRevision = 1, EntityId = Guid.NewGuid(), EntityKind = DeletedEntityKind.Server,
            Action = "Upsert", BaseRevision = 0, CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Value = new ConfigurationValue { Name = "server", Endpoint = "https://test.invalid/health", Generation = Guid.NewGuid() }
        };
    }

    public RevisionImage WithProjectionRows()
    {
        using UptimeHarborDbContext context = CreateContext();
        ApplicationUser user = new(Actor, "admin") { AccessFailedCount = 4, LockoutEnd = DateTimeOffset.MaxValue };
        user.ApplySharedAccount(RevisionAccounts.Materialize(Baseline.Values[Actor]));
        object[] entities = [user, new ApplicationRole(Role, SharedAccount.ADMINISTRATOR),
            new IdentityUserRole<Guid> { RoleId = Role, UserId = Actor }];
        return Baseline with
        {
            ProjectionRows = entities.Select(entity => SnapshotSchema.Capture(context,
                context.Model.FindEntityType(entity.GetType())!, entity)).ToImmutableArray()
        };
    }

    public ClusterOptions Options(string path)
    {
        return new ClusterOptions
        {
            StoragePath = path, NodeId = Node, ClusterId = Baseline.ClusterId, Generation = 1,
            StorageGeneration = Guid.NewGuid(), SnapshotChunkMaxBytes = 16384
        };
    }

    public static UptimeHarborDbContext CreateContext()
    {
        return new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
    }
}
