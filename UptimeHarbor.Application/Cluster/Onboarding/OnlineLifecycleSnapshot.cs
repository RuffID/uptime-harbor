using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnlineLifecycleSnapshot
{
    public static void Validate(RevisionImage image)
    {
        foreach (KeyValuePair<Guid, AcceptedOnlineGrant> item in image.OnboardingContracts?.OnlineGrants ?? [])
        {
            AcceptedOnlineGrant accepted = item.Value;
            OnlineGrant grant = accepted.Grant;
            OnlineLifecycleCommands.ValidateGrant(grant);
            CommandResult? authorized = image.Results.GetValueOrDefault(grant.GrantId);
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(grant.NodeId);
            if (item.Key != grant.GrantId || grant.ClusterId != image.ClusterId || grant.Generation != image.Generation
                || accepted.AuthorizedIndex <= 0 || accepted.AuthorizedIndex > image.Index
                || authorized?.Outcome != OperationOutcome.Committed || authorized.Index != accepted.AuthorizedIndex
                || authorized.Digest != accepted.AuthorizedDigest || node is null || node.StorageGeneration != grant.StorageGeneration
                || image.Operations.Credentials.GetValueOrDefault(grant.CredentialId)?.NodeId != grant.NodeId
                || image.OnboardingContracts!.Issuers?.ContainsKey(grant.IssuerEpoch) != true
                || accepted.ConsumedIndex.HasValue != (accepted.ConsumedDigest is not null)
                || accepted.ProcessRenewal && (grant.Purpose != OnlineGrantPurpose.Process || accepted.ConsumedIndex.HasValue))
            {
                throw new InvalidDataException("OnlineGrantSnapshotInvalid");
            }

            if (accepted.ConsumedIndex.HasValue)
            {
                CommandResult? consumed = image.Results.GetValueOrDefault(OnlineLifecycleCommands.ConsumeId(grant.GrantId));
                if (consumed?.Outcome != OperationOutcome.Committed || consumed.Index != accepted.ConsumedIndex
                    || consumed.Digest != accepted.ConsumedDigest || consumed.Index <= accepted.AuthorizedIndex || consumed.Index > image.Index
                    || grant.Purpose is not (OnlineGrantPurpose.Activation or OnlineGrantPurpose.Enable))
                {
                    throw new InvalidDataException("OnlineGrantConsumptionSnapshotInvalid");
                }
            }
        }

        foreach (KeyValuePair<Guid, OnlineProcessLease> item in image.OnboardingContracts?.OnlineProcesses ?? [])
        {
            OnlineProcessLease process = item.Value;
            AcceptedOnlineGrant? accepted = image.OnboardingContracts!.OnlineGrants?.GetValueOrDefault(process.GrantId);
            OnlineGrant? grant = accepted?.Grant;
            if (item.Key != process.NodeId || grant is null || grant.Purpose != OnlineGrantPurpose.Process
                || process.Index != accepted!.AuthorizedIndex || process.NodeId != grant.NodeId || process.BootId != grant.BootId
                || process.BootChallenge != grant.BootChallenge || process.ProcessEpoch != grant.ProcessEpoch
                || process.RecoveryVersion != grant.RecoveryVersion || process.LeaseUntilUtc.Offset != TimeSpan.Zero
                || process.LeaseUntilUtc <= grant.IssuedAtUtc || process.LeaseUntilUtc > grant.ExpiresAtUtc.AddMinutes(2))
            {
                throw new InvalidDataException("OnlineProcessSnapshotInvalid");
            }
        }
    }
}
