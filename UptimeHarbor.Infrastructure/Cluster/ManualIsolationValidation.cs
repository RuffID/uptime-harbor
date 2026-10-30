using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Проверяет полноту подписанной ручной проверки; не заменяет фактическую работу специалиста.</summary>
public static class ManualIsolationValidation
{
    public static string Validate(ManualIsolationConfirmation proof, IReadOnlyDictionary<Guid, Guid> instances,
        DateTimeOffset now, long botId, bool disaster)
    {
        bool fresh = proof.VerifiedAtUtc.Offset == TimeSpan.Zero && proof.VerifiedAtUtc <= now
            && now - proof.VerifiedAtUtc <= TimeSpan.FromMinutes(15);
        if (proof.ProcedureId == Guid.Empty || proof.OperatorId == Guid.Empty || !fresh
            || !ClusterOptionsValidator.IsFingerprint(proof.ExternalRecordDigest)
            || proof.Instances.Count != instances.Count || proof.Instances.Select(item => item.NodeId).Distinct().Count() != instances.Count)
        {
            throw new InvalidDataException("IsolationConfirmationIncomplete");
        }

        foreach (IsolatedClusterInstance instance in proof.Instances)
        {
            bool exact = instances.TryGetValue(instance.NodeId, out Guid storage) && storage == instance.StorageGeneration;
            bool isolated = instance.Stopped && instance.AutostartDisabled && instance.PeerIngressBlocked
                && instance.BrowserIngressBlocked && instance.CopiesQuarantined;
            if (!exact || !isolated || !ClusterOptionsValidator.IsFingerprint(instance.EvidenceDigest))
            {
                throw new InvalidDataException("FormerInstanceNotIsolated");
            }
        }

        if (disaster && (botId <= 0 || proof.BotId != botId || !proof.PreviousBotTokenRevoked || !proof.PreviousPeerTrustRevoked))
        {
            throw new InvalidDataException("ExternalCredentialsNotRevoked");
        }

        return RevisionCodec.Digest(JsonSerializer.SerializeToUtf8Bytes(proof));
    }
}
