using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Передача полномочий только тому процессу, для которого принят точный Bind.</summary>
internal static class VoterContinuationHandoff
{
    internal static bool AllowsRenewal(RevisionImage image, OnlineProcessLease process, OnlineGrant grant, DateTimeOffset now)
    {
        VoterCredentialRotation? rotation = VoterCredentialPolicy.Latest(image, grant.NodeId);
        if (grant.Purpose != OnlineGrantPurpose.Process || rotation?.ContinuationRequestId is not Guid requestId
            || grant.SnapshotDigest != rotation.BindDigest || grant.MinimumIndex < rotation.CredentialRevision
            || now >= process.LeaseUntilUtc || !MatchesProcess(process, grant) || !MatchesLeaf(rotation, grant))
        {
            return false;
        }

        AcceptedVoterContinuation? accepted = image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(requestId);
        if (accepted?.Registration is not { } registration || accepted.Bind is not { } bind
            || !VoterContinuationBinding.IsCurrentBinding(image, bind))
        {
            return false;
        }

        DateTimeOffset boundAt = bind.VoterContinuation!.Acceptance!.AtUtc;
        RevisionImage beforeBind = VoterContinuationBinding.BeforeBinding(image, grant.CredentialId);
        return process.GrantId == registration.OperationId && grant.IssuedAtUtc >= boundAt
            && VoterContinuationBinding.Refusal(image, bind, boundAt, image.Index, replay: true) is null
            && VoterContinuationReducer.Refusal(beforeBind, registration, now, image.Index, replay: true) is null;
    }

    internal static bool MatchesProcess(OnlineProcessLease process, OnlineGrant grant)
    {
        return process.NodeId == grant.NodeId && process.BootId == grant.BootId && process.BootChallenge == grant.BootChallenge
            && process.ProcessEpoch == grant.ProcessEpoch && process.RecoveryVersion == grant.RecoveryVersion;
    }

    internal static bool MatchesLeaf(VoterCredentialRotation rotation, OnlineGrant grant)
    {
        EnrollmentIssuance leaf = rotation.Renewal.Issuance;
        return leaf.IssueId == grant.CredentialId && leaf.NodeId == grant.NodeId
            && leaf.StorageGeneration == grant.StorageGeneration && leaf.PeerUri == grant.PeerUri
            && leaf.IssuerEpoch == grant.IssuerEpoch && leaf.SpkiDigest == grant.SpkiDigest;
    }
}
