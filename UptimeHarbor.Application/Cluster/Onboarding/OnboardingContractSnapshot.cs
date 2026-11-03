using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnboardingContractSnapshot
{
    public static void Validate(RevisionImage image)
    {
        EnrollmentTrustSnapshot.Validate(image);
        OnlineLifecycleSnapshot.Validate(image);
        OnboardingContractImage? data = image.OnboardingContracts;
        if (data is null)
        {
            return;
        }

        if (data.Version != 1 || data.LegacyHistory.IsDefault || data.SenderOutages.IsDefault
            || data.SummaryCuts.IsDefault || data.CoverageReceipts.IsDefault)
        {
            throw new InvalidDataException("OnboardingContractsSnapshotInvalid");
        }

        if (data.Genesis is not null)
        {
            OnboardingDataCodec.Validate(data.Genesis);
            if (data.Genesis.ClusterId != image.ClusterId || data.Genesis.Generation != image.Generation)
            {
                throw new InvalidDataException("GenesisClusterMismatch");
            }
        }

        if (data.Import is not null)
        {
            OnboardingDataCodec.Validate(data.Import);
            if (data.Genesis?.MigrationId != data.Import.MigrationId || data.Genesis.GenesisId != data.Import.GenesisId)
            {
                throw new InvalidDataException("ImportGenesisMismatch");
            }
        }

        foreach (LegacyHistory history in data.LegacyHistory)
        {
            OnboardingDataCodec.Validate(history);
            if (data.Import?.MigrationId != history.MigrationId)
            {
                throw new InvalidDataException("LegacyImportMismatch");
            }
        }

        if (data.SenderAssignment is not null)
        {
            OnboardingDataCodec.Validate(data.SenderAssignment);
            if (data.SenderAssignment.ClusterId != image.ClusterId || data.SenderAssignment.Generation != image.Generation)
            {
                throw new InvalidDataException("SenderAssignmentClusterMismatch");
            }
        }

        foreach (SenderOutage outage in data.SenderOutages)
        {
            OnboardingDataCodec.Validate(outage);
        }

        foreach (SummaryCut cut in data.SummaryCuts)
        {
            OnboardingDataCodec.Validate(cut);
            if (cut.Index > image.Index || cut.Term > image.Term
                || !data.SenderOutages.Any(outage => outage.OutageId == cut.OutageId && outage.AssignmentRevision == cut.AssignmentRevision))
            {
                throw new InvalidDataException("SummaryCutSnapshotMismatch");
            }
        }

        foreach (CoverageReceipt receipt in data.CoverageReceipts)
        {
            OnboardingDataCodec.Validate(receipt);
            if (!data.SummaryCuts.Any(cut => cut.CutId == receipt.CutId && cut.OutageId == receipt.OutageId && cut.Index == receipt.CoveredIndex))
            {
                throw new InvalidDataException("CoverageCutSnapshotMismatch");
            }
        }

        bool duplicates = data.LegacyHistory.Select(item => item.PartitionId).Distinct().Count() != data.LegacyHistory.Length
            || data.SenderOutages.Select(item => item.OutageId).Distinct().Count() != data.SenderOutages.Length
            || data.SummaryCuts.Select(item => item.CutId).Distinct().Count() != data.SummaryCuts.Length
            || data.CoverageReceipts.Select(item => item.ReceiptId).Distinct().Count() != data.CoverageReceipts.Length;
        if (duplicates)
        {
            throw new InvalidDataException("OnboardingContractsSnapshotDuplicate");
        }
    }
}
