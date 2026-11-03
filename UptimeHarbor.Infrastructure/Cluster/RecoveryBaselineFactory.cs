using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Переобозначает выбранный срез, сохраняя реальные результаты, payload и прежнюю authority попыток.</summary>
public static class RecoveryBaselineFactory
{
    public static RevisionImage Create(RevisionImage source, ClusterOptions target, EmergencyRecoverySelection selection,
        OperationalNode node, OperationalCredential credential, string isolationDigest, DateTimeOffset at)
    {
        bool newIdentity = target.ClusterId != source.ClusterId && target.Generation > source.Generation
            && !source.Monitoring.Nodes.ContainsKey(target.NodeId)
            && !source.Monitoring.Nodes.Values.Contains(target.StorageGeneration);
        if (!newIdentity || node.NodeId != target.NodeId || node.StorageGeneration != target.StorageGeneration
            || !node.Voting || node.Activated || node.Removed || node.Address != target.PublicUri
            || credential.NodeId != node.NodeId || credential.Id != target.CredentialId
            || source.Operations.Credentials.ContainsKey(credential.Id) || credential.RevokedAtUtc.HasValue
            || credential.AdmissionId.HasValue || credential.ExpiresAtUtc <= at)
        {
            throw new InvalidDataException("RecoveryRequiresNewIdentity");
        }

        ImmutableDictionary<Guid, OperationalNode> nodes = source.Operations.Nodes.ToImmutableDictionary(
            item => item.Key, item => item.Value with { Voting = false, Activated = false, Removed = true });
        ClusterOperationsImage operations = new()
        {
            Nodes = nodes.Add(node.NodeId, node with { Revision = source.Index }),
            Credentials = ImmutableDictionary<Guid, OperationalCredential>.Empty.Add(credential.Id, credential),
            Revision = source.Index, MaintenanceId = selection.RecoveryId, BotId = source.Operations.BotId
        };
        RecoveryBaseline baseline = new(selection.RecoveryId, source.ClusterId, source.Generation, source.Index, source.Term,
            selection.SnapshotDigest, selection.KnownCommittedIndex, selection.AdministratorId, selection.Isolation.OperatorId,
            isolationDigest, at, source.Operations)
        {
            PreviousOnboarding = source.Onboarding, PreviousOnboardingContracts = source.OnboardingContracts,
            Attempts = source.Monitoring.Attempts.ToImmutableDictionary(item => item.Key, item =>
                new RecoveredAttemptIdentity(item.Value.IntentId, item.Value.Number, item.Value.Authority, item.Value.StartedAtUtc))
        };
        if (source.RecoveryBaseline is not null)
        {
            baseline = baseline with { Ancestors = [source.RecoveryBaseline] };
        }

        RevisionImage result = source with
        {
            ClusterId = target.ClusterId, Generation = target.Generation, BootstrapAccountDigest = null,
            RecoveryBaseline = baseline, Operations = operations, Onboarding = null, OnboardingContracts = null,
            Monitoring = source.Monitoring with { Nodes = source.Monitoring.Nodes.Add(target.NodeId, target.StorageGeneration) }
        };
        HashSet<string> operationalTypes = [typeof(ClusterMember).FullName!, typeof(PeerTrustCredential).FullName!, typeof(MembershipChange).FullName!, typeof(OnboardingProjection).FullName!, typeof(EnrollmentIssuer).FullName!];
        ImmutableArray<SharedSnapshotRow>.Builder rows = ImmutableArray.CreateBuilder<SharedSnapshotRow>();
        foreach (SharedSnapshotRow row in source.ProjectionRows.Where(row => !operationalTypes.Contains(row.Entity)))
        {
            ImmutableDictionary<string, JsonElement> fields = row.Fields;
            if (fields.ContainsKey("ClusterId"))
            {
                if (fields["ClusterId"].GetGuid() != source.ClusterId || fields["ClusterGeneration"].GetInt64() != source.Generation)
                {
                    throw new InvalidDataException("RecoveryProjectionIdentityMismatch");
                }

                fields = fields.SetItem("ClusterId", JsonSerializer.SerializeToElement(target.ClusterId))
                    .SetItem("ClusterGeneration", JsonSerializer.SerializeToElement(target.Generation));
            }

            rows.Add(row with { Fields = fields });
        }

        // Metadata модели не открывает connection и не выполняет SQL.
        using var context = new Persistence.UptimeHarborDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Persistence.UptimeHarborDbContext>()
            .UseSqlServer().Options);
        foreach (ProjectionRecord record in ClusterOperationsProjection.Changes(new(target.ClusterId, target.Generation), result))
        {
            rows.Add(SnapshotSchema.Capture(context, context.Model.FindEntityType(record.GetType())!, record));
        }

        result = result with { ProjectionRows = rows.ToImmutable() };
        SnapshotImageValidation.Validate(context, result);
        return result;
    }
}
