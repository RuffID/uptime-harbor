using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Связывает native-only snapshot с принятыми Bind и process lease.</summary>
internal sealed record LocalVoterContinuationQuarantine(string Reference, RevisionCommand Bind, OnlineProcessLease Original)
{
    internal bool Matches(RevisionImage image, DateTimeOffset now)
    {
        VoterContinuationContext context = Bind.VoterContinuation!.Context;
        VoterCredentialRenewal renewal = context.Request.Renewal;
        OnboardingContractImage? contracts = image.OnboardingContracts;
        AcceptedVoterContinuation? accepted = contracts?.VoterContinuations?.GetValueOrDefault(context.Request.OperationId);
        OnlineProcessLease? process = contracts?.OnlineProcesses?.GetValueOrDefault(Original.NodeId);
        if (accepted?.Bind != Bind || process is null || now >= process.LeaseUntilUtc
            || image.Operations.Nodes.GetValueOrDefault(Original.NodeId) is not { Voting: true, Removed: false } node
            || node.StorageGeneration != renewal.Issuance.StorageGeneration || node.Address != renewal.Issuance.PeerUri
            || image.Operations.MembershipVersion != context.MembershipVersion || image.Operations.MaintenanceId != Guid.Empty
            || image.Operations.Changes.Values.Any(change => !change.ConfirmedVersion.HasValue)
            || image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId) is not { RevokedAtUtc: null } previous
            || previous.Fingerprint != renewal.PreviousFingerprint
            || contracts?.Issuers?.GetValueOrDefault(context.PreviousIssuerEpoch) is not { RevokedAtUtc: null } previousIssuer
            || previousIssuer.Revision != context.PreviousIssuerRevision
            || !VoterContinuationBinding.IsCurrentBinding(image, Bind))
        {
            return false;
        }

        return process == Original || HasAcceptedHandoff(image, Bind, Original, process);
    }

    internal static bool HasAcceptedHandoff(RevisionImage image, RevisionCommand bind, OnlineProcessLease original,
        OnlineProcessLease? current)
    {
        var grants = image.OnboardingContracts?.OnlineGrants;
        if (current is not { CredentialContinuation: false } || grants?.GetValueOrDefault(current.GrantId) is not { } accepted
            || accepted.AuthorizedIndex != current.Index || !VoterContinuationHandoff.MatchesProcess(current, accepted.Grant))
        {
            return false;
        }

        VoterCredentialRotation? rotation = VoterCredentialPolicy.Latest(image, original.NodeId);
        if (rotation is null || rotation.BindOperationId != bind.OperationId || !IsExactProcessGrant(accepted, rotation))
        {
            return false;
        }

        if (IsHandoff(accepted, rotation))
        {
            return true;
        }

        return grants.Values.Any(grant => grant.AuthorizedIndex < current.Index
            && IsHandoff(grant, rotation) && IsExactProcessGrant(grant, rotation));

        bool IsExactProcessGrant(AcceptedOnlineGrant candidate, VoterCredentialRotation expected)
        {
            OnlineGrant grant = candidate.Grant;
            CommandResult? result = image.Results.GetValueOrDefault(grant.GrantId);
            return grant.Purpose == OnlineGrantPurpose.Process && VoterContinuationHandoff.MatchesProcess(original, grant)
                && VoterContinuationHandoff.MatchesLeaf(expected, grant)
                && grant.LeafDigest == bind.VoterContinuation!.Credential!.Fingerprint
                && result is { Outcome: OperationOutcome.Committed } && result.Index == candidate.AuthorizedIndex
                && result.Index > expected.CredentialRevision && result.Digest == candidate.AuthorizedDigest;
        }

        static bool IsHandoff(AcceptedOnlineGrant candidate, VoterCredentialRotation expected)
        {
            return candidate.ProcessRenewal && candidate.Grant.SnapshotDigest == expected.BindDigest
                && candidate.Grant.MinimumIndex >= expected.CredentialRevision;
        }
    }
}
