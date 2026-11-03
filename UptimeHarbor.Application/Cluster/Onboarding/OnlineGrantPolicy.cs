using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Актуальная accepted authority, отдельно от криптографической подписи и локальной готовности.</summary>
public static class OnlineGrantPolicy
{
    public static bool Current(RevisionImage image, OnlineGrant grant, DateTimeOffset now, bool registeringProcess = false,
        bool consumed = false, bool processRenewal = false)
    {
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(grant.NodeId);
        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(grant.CredentialId);
        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(grant.IssuerEpoch);
        bool exact = image.ClusterId == grant.ClusterId && image.Generation == grant.Generation
            && image.Operations.MaintenanceId == Guid.Empty && image.Operations.Revision == grant.OperationsRevision
            && image.Operations.MembershipVersion == grant.MembershipVersion && image.Index >= grant.MinimumIndex
            && !image.Operations.Changes.Values.Any(change => !change.ConfirmedVersion.HasValue)
            && node is not null && !node.Removed && node.StorageGeneration == grant.StorageGeneration && node.Address == grant.PeerUri
            && credential is not null && credential.NodeId == grant.NodeId && credential.Fingerprint == grant.LeafDigest
            && !credential.RevokedAtUtc.HasValue && now < credential.ExpiresAtUtc
            && issuer is not null && !issuer.RevokedAtUtc.HasValue
            && (consumed || processRenewal || !issuer.RetiredAtUtc.HasValue)
            && now >= issuer.NotBeforeUtc && now < issuer.NotAfterUtc
            && (consumed || grant.IssuedAtUtc <= now && now < grant.ExpiresAtUtc)
            && OnboardingReducer.Active(image, grant.ActorId, grant.ActorRevision);
        if (!exact)
        {
            return false;
        }

        if (grant.InvitationId.HasValue)
        {
            OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(grant.InvitationId.Value)?.Attempt;
            if (attempt is null || attempt.Request.AttemptId != grant.AttemptId || attempt.RequestDigest != grant.RequestDigest
                || attempt.Request.NodeId != grant.NodeId || attempt.Request.SpkiDigest != grant.SpkiDigest
                || attempt.AdmitOperationId != grant.AdmitOperationId || attempt.IssueId != grant.CredentialId
                || attempt.CertificateDigest != grant.LeafDigest || attempt.Issuance?.IssuerEpoch != grant.IssuerEpoch
                || attempt.BootId != grant.BootId || attempt.BootChallenge != grant.BootChallenge || attempt.ProcessEpoch != grant.ProcessEpoch
                || attempt.Delegation.ActorId != grant.ActorId || attempt.Delegation.ActorRevision != grant.ActorRevision
                || attempt.Delegation.Revision != grant.DelegationRevision
                || attempt.Status is OnboardingAttemptStatus.Cancelled or OnboardingAttemptStatus.PauseAwaitingAdministrator
                || now >= attempt.ProcessLeaseUntilUtc || now - attempt.LastProgressAtUtc >= TimeSpan.FromHours(24))
            {
                return false;
            }
        }
        else
        {
            GenesisAuthorization? genesis = image.OnboardingContracts?.Genesis;
            if (image.OnboardingContracts?.GenesisInitialized != true || genesis is null
                || genesis.GenesisId != grant.AttemptId || genesis.NodeId != grant.NodeId
                || genesis.StorageGeneration != grant.StorageGeneration || grant.AdmitOperationId.HasValue
                || grant.DelegationRevision != 0 || genesis.AccountOrImportDigest != grant.RequestDigest)
            {
                return false;
            }
        }

        if (grant.Purpose != OnlineGrantPurpose.CatchUp && !node!.Voting)
        {
            return false;
        }

        if (grant.Purpose == OnlineGrantPurpose.Enable && !node!.Activated)
        {
            return false;
        }

        if (!registeringProcess)
        {
            OnlineProcessLease? process = image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(grant.NodeId);
            return process is not null && process.BootId == grant.BootId && process.BootChallenge == grant.BootChallenge
                && process.ProcessEpoch == grant.ProcessEpoch && process.RecoveryVersion == grant.RecoveryVersion
                && now < process.LeaseUntilUtc;
        }

        return true;
    }
}
