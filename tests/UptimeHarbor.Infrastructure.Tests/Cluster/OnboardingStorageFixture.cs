using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class OnboardingStorageFixture
{
    public RevisionFixture Source { get; } = new();
    public Guid InvitationId { get; } = Guid.NewGuid();
    public Guid Storage { get; } = Guid.NewGuid();
    public DateTimeOffset Now { get; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    public Guid Code { get; } = InvitationSecrets.CreateCode();
    public RevisionImage Image { get; }

    public OnboardingStorageFixture()
    {
        OperationalNode node = new(Source.Node, Storage, "https://first.invalid/internal/raft", "first", true, false, false, 1);
        RevisionImage initial = Source.Baseline with
        {
            Operations = new ClusterOperationsImage { Nodes = new ClusterOperationsImage().Nodes.Add(node.NodeId, node), MembershipVersion = 1 },
            Monitoring = new MonitoringImage { Nodes = new MonitoringImage().Nodes.Add(node.NodeId, Storage) }
        };
        RevisionCommand issue = Source.Server() with
        {
            Schema = 2, OperationId = InvitationId, EntityId = InvitationId, EntityKind = DeletedEntityKind.Node,
            Action = "IssueInvitation", Value = null, CreatedAtUtc = Now,
            Onboarding = new OnboardingCommand
            {
                StorageGeneration = Storage, InvitationId = InvitationId, CoordinatorAtUtc = Now, CoordinatorTerm = 1,
                ReauthenticatedAtUtc = Now, Verifier = InvitationSecrets.Verifier(initial.ClusterId, 1, InvitationId, Code)
            }
        };
        Image = new RevisionReducer().Apply(initial, issue, 3, 1);
        Xunit.Assert.Equal(OperationOutcome.Committed, Image.Results[InvitationId].Outcome);
    }

    public RevisionCommand Revoke()
    {
        return Source.Server() with
        {
            Schema = 2, EntityId = InvitationId, EntityKind = DeletedEntityKind.Node, Action = "RevokeInvitation", Value = null,
            BaseRevision = Image.Onboarding!.Revision, CreatedAtUtc = Now,
            Onboarding = new OnboardingCommand
            {
                StorageGeneration = Storage, InvitationId = InvitationId, ReauthenticatedAtUtc = Now,
                CoordinatorAtUtc = Now, CoordinatorTerm = 2
            }
        };
    }

    public RevisionImage WithRows(RevisionImage image)
    {
        using UptimeHarborDbContext context = RevisionFixture.CreateContext();
        List<SharedSnapshotRow> rows = [.. Source.WithProjectionRows().ProjectionRows];
        RevisionImage empty = new(image.ClusterId, image.Generation);
        foreach (ProjectionRecord record in ClusterOperationsProjection.Changes(empty, image).Concat(OnboardingSnapshot.Changes(empty, image)))
        {
            rows.Add(SnapshotSchema.Capture(context, context.Model.FindEntityType(record.GetType())!, record));
        }

        return image with { ProjectionRows = rows.ToImmutableArray() };
    }

    public ClusterOptions Options(string path)
    {
        return new ClusterOptions
        {
            Mode = "Restart", ClusterId = Image.ClusterId, Generation = Image.Generation, NodeId = Source.Node,
            StorageGeneration = Storage, StoragePath = path, SnapshotChunkMaxBytes = 16384,
            ProtocolMajor = 2, CommandSchema = 2, SnapshotFormat = 2, SqlSchema = 2
        };
    }
}
