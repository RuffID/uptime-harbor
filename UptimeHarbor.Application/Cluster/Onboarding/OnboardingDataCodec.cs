using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Общие форматы будущих import/sender шагов без исполнения этих сценариев.</summary>
public static class OnboardingDataCodec
{
    public static byte[] Encode<T>(T data)
    {
        Validate(data);
        return StrictOnboardingJson.Encode(data);
    }

    public static T Decode<T>(ReadOnlySpan<byte> bytes)
    {
        T data = StrictOnboardingJson.Decode<T>(bytes);
        Validate(data);
        return data;
    }

    public static void Validate<T>(T data)
    {
        switch (data)
        {
            case GenesisAuthorization genesis:
                Version(genesis.Version);
                Ids(genesis.GenesisId, genesis.InstallIdentity, genesis.ClusterId, genesis.NodeId, genesis.StorageGeneration);
                Positive(genesis.Generation);
                ClusterOperationValidation.ValidateAddress(genesis.PeerUri);
                Digests(genesis.IssuerSpkiDigest, genesis.IssuerCertificateDigest, genesis.AccountOrImportDigest);
                if (genesis.MigrationId.HasValue)
                {
                    Ids(genesis.MigrationId.Value);
                }

                break;
            case ImportBaselineRef baseline:
                Version(baseline.Version);
                Ids(baseline.MigrationId, baseline.GenesisId);
                Digests(baseline.ManifestDigest, baseline.BaselineDigest);
                Positive(baseline.BaselineBytes);
                StorageGuards.Text(baseline.SourceCut, 128, nameof(baseline.SourceCut));
                StorageGuards.Text(baseline.SchemaVersion, 64, nameof(baseline.SchemaVersion));
                break;
            case LegacyHistory history:
                Version(history.Version);
                Ids(history.MigrationId, history.PartitionId);
                ArgumentOutOfRangeException.ThrowIfNegative(history.RowCount);
                StorageGuards.Text(history.SourceCut, 128, nameof(history.SourceCut));
                if (history.SourceKind is not ("Outbox" or "Observation" or "Incident" or "Delivery"))
                {
                    throw new InvalidDataException("LegacySourceUnsupported");
                }

                StrictOnboardingJson.Validate(history.CanonicalPayload);
                if (Convert.ToHexString(SHA256.HashData(history.CanonicalPayload)) != history.PayloadDigest)
                {
                    throw new InvalidDataException("LegacyPartitionDigestMismatch");
                }

                break;
            case SenderAssignment assignment:
                Version(assignment.Version);
                Ids(assignment.ClusterId, assignment.SenderNodeId, assignment.StorageGeneration, assignment.SenderEpoch);
                Positive(assignment.Generation, assignment.BotId, assignment.AssignmentRevision);
                break;
            case SenderOutage outage:
                Version(outage.Version);
                Ids(outage.OutageId);
                Positive(outage.AssignmentRevision);
                StorageGuards.Utc(outage.DetectedAtUtc);
                if (outage.State is not ("Open" or "Planning" or "Delivering" or "Acknowledged") || outage.ChatCoverage is null)
                {
                    throw new InvalidDataException("SenderOutageInvalid");
                }

                foreach (KeyValuePair<Guid, long> coverage in outage.ChatCoverage)
                {
                    Ids(coverage.Key);
                    ArgumentOutOfRangeException.ThrowIfNegative(coverage.Value);
                }

                break;
            case SummaryCut cut:
                Version(cut.Version);
                Ids(cut.OutageId, cut.CutId);
                Positive(cut.Index, cut.Term, cut.AssignmentRevision);
                ArgumentOutOfRangeException.ThrowIfNegative(cut.MembershipVersion);
                Digests(cut.ImmutableStateDigest);
                if (cut.SourceFrontiers is null || cut.SourceFrontiers.Count > 10000)
                {
                    throw new InvalidDataException("SummaryFrontiersInvalid");
                }

                foreach (KeyValuePair<string, long> frontier in cut.SourceFrontiers)
                {
                    StorageGuards.Text(frontier.Key, 128, nameof(frontier.Key));
                    ArgumentOutOfRangeException.ThrowIfNegative(frontier.Value);
                }

                break;
            case CoverageReceipt receipt:
                Version(receipt.Version);
                Ids(receipt.ReceiptId, receipt.OutageId, receipt.CutId, receipt.ChatId);
                Positive(receipt.BindingCoverageRevision, receipt.CoveredIndex);
                if (receipt.PreviousIndex < 0 || receipt.CoveredIndex < receipt.PreviousIndex
                    || receipt.PartIds.IsDefault || receipt.AcceptedResultIds.IsDefault
                    || receipt.PartIds.Length != receipt.AcceptedResultIds.Length
                    || receipt.PartIds.Distinct().Count() != receipt.PartIds.Length
                    || receipt.AcceptedResultIds.Distinct().Count() != receipt.AcceptedResultIds.Length)
                {
                    throw new InvalidDataException("CoverageReceiptInvalid");
                }

                Ids([.. receipt.PartIds, .. receipt.AcceptedResultIds]);
                break;
            default:
                throw new InvalidDataException("OnboardingContractUnsupported");
        }
    }

    private static void Version(int version)
    {
        if (version != 1)
        {
            throw new InvalidDataException("OnboardingContractVersionUnsupported");
        }
    }

    private static void Ids(params Guid[] ids)
    {
        foreach (Guid id in ids)
        {
            StorageGuards.Id(id, "Identity");
        }
    }

    private static void Digests(params string[] values)
    {
        foreach (string value in values)
        {
            if (StorageGuards.Digest(value) != value)
            {
                throw new InvalidDataException("OnboardingDigestNotCanonical");
            }
        }
    }

    private static void Positive(params long[] values)
    {
        foreach (long value in values)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        }
    }
}
