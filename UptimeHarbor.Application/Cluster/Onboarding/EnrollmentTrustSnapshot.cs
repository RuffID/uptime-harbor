using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class EnrollmentTrustSnapshot
{
    public static void Validate(RevisionImage image)
    {
        OnboardingContractImage? contracts = image.OnboardingContracts;
        if (contracts is null)
        {
            return;
        }

        if (contracts.GenesisInitialized && (contracts.Genesis is null || contracts.Issuers is null || contracts.Issuers.IsEmpty))
        {
            throw new InvalidDataException("EnrollmentGenesisSnapshotInvalid");
        }

        HashSet<string> pins = new(StringComparer.Ordinal);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<Guid, AcceptedEnrollmentIssuer> item in contracts.Issuers ?? [])
        {
            AcceptedEnrollmentIssuer issuer = item.Value;
            EnrollmentTrustCommands.ValidateIssuer(issuer);
            OperationalNode? owner = image.Operations.Nodes.GetValueOrDefault(issuer.NodeId);
            if (item.Key != issuer.Epoch || issuer.Revision <= 0 || issuer.Revision > image.Index
                || owner is null || owner.StorageGeneration != issuer.StorageGeneration
                || !pins.Add(issuer.CertificateDigest) || !keys.Add(issuer.SpkiDigest))
            {
                throw new InvalidDataException("EnrollmentIssuerSnapshotMismatch");
            }
        }
    }

    public static IReadOnlyList<ProjectionRecord> Changes(RevisionImage before, RevisionImage after)
    {
        List<ProjectionRecord> records = [];
        foreach (AcceptedEnrollmentIssuer issuer in after.OnboardingContracts?.Issuers?.Values ?? [])
        {
            if (before.OnboardingContracts?.Issuers?.GetValueOrDefault(issuer.Epoch) == issuer)
            {
                continue;
            }

            JournalPosition position = new(new ClusterIdentity(after.ClusterId, after.Generation), issuer.Revision, after.Term);
            EnrollmentIssuer record = new(issuer.Epoch, position, issuer.NodeId, issuer.StorageGeneration,
                issuer.CertificateDer, issuer.CertificateDigest, issuer.SpkiDigest, issuer.NotBeforeUtc, issuer.NotAfterUtc);
            if (issuer.RetiredAtUtc.HasValue)
            {
                record.Retire(issuer.RetiredAtUtc.Value);
            }

            if (issuer.RevokedAtUtc.HasValue)
            {
                record.Revoke(issuer.RevokedAtUtc.Value);
            }

            records.Add(record);
        }

        return records;
    }
}
