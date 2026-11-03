using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnlineLifecycleCommands
{
    public static bool IsOnline(RevisionCommand command)
    {
        return command.Action is "RegisterOnlineProcess" or "RenewOnlineProcess" or "AuthorizeOnboardingActivation"
            or "ConsumeOnboardingActivation" or "AuthorizeOnlineEnable" or "EnableOnlineGrant";
    }

    public static void Validate(RevisionCommand command)
    {
        OnlineLifecycleCommand data = command.OnlineLifecycle ?? throw new InvalidDataException("OnlineLifecycleMissing");
        OnlineGrant grant = data.Grant;
        ValidateGrant(grant);
        OnlineGrantPurpose purpose = OnlineGrantPurpose.Activation;
        if (command.Action is "RegisterOnlineProcess" or "RenewOnlineProcess")
        {
            purpose = OnlineGrantPurpose.Process;
        }
        else if (command.Action is "AuthorizeOnlineEnable" or "EnableOnlineGrant")
        {
            purpose = OnlineGrantPurpose.Enable;
        }

        bool mixed = command.Onboarding is not null || command.EnrollmentTrust is not null || command.Operation is not null
            || command.Value is not null || command.Monitoring is not null || command.Delivery is not null
            || command.Target is not null || command.Replacement is not null || command.AccountAction is not null
            || command.ResolvesConflictId.HasValue;
        if (!IsOnline(command) || mixed || command.Schema != 3 || data.Version != 1 || grant.Purpose != purpose
            || command.SystemSource != "OnlineLifecycle" || command.ActorUserId != Guid.Empty || command.ActorRevision != 0
            || command.EntityId != grant.NodeId || command.EntityKind != DeletedEntityKind.Node
            || command.ClusterId != grant.ClusterId || command.ClusterGeneration != grant.Generation
            || command.BaseRevision != grant.OperationsRevision || data.StorageGeneration == Guid.Empty
            || grant.Signature.Length is < 12 or > 112)
        {
            throw new InvalidDataException("OnlineLifecycleEnvelopeInvalid");
        }

        if (data.CoordinatorAtUtc.HasValue)
        {
            StorageGuards.Utc(data.CoordinatorAtUtc.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.CoordinatorTerm);
        }
        else if (data.CoordinatorTerm != 0)
        {
            throw new InvalidDataException("OnlineLifecycleTimestampMissing");
        }
    }

    public static void ValidateGrant(OnlineGrant grant)
    {
        foreach (Guid id in new[] { grant.ClusterId, grant.AttemptId, grant.NodeId, grant.StorageGeneration,
            grant.CredentialId, grant.IssuerEpoch, grant.GrantId, grant.BootId, grant.ActorId })
        {
            StorageGuards.Id(id, nameof(grant));
        }

        foreach (string digest in new[] { grant.SpkiDigest, grant.LeafDigest, grant.RequestDigest })
        {
            StorageGuards.Digest(digest);
        }

        ClusterOperationValidation.ValidateAddress(grant.PeerUri);
        StorageGuards.Utc(grant.IssuedAtUtc);
        StorageGuards.Utc(grant.ExpiresAtUtc);
        if (grant.Version != 1 || !Enum.IsDefined(grant.Purpose) || grant.Generation <= 0 || grant.ProcessEpoch <= 0
            || grant.ActorRevision <= 0 || grant.DelegationRevision < 0 || grant.RecoveryVersion < 0
            || grant.OperationsRevision <= 0 || grant.MembershipVersion < 0 || grant.MinimumIndex <= 0
            || !OnboardingCommands.IsBootChallenge(grant.BootChallenge)
            || grant.ExpiresAtUtc <= grant.IssuedAtUtc || grant.ExpiresAtUtc - grant.IssuedAtUtc > TimeSpan.FromMinutes(2)
            || grant.InvitationId == Guid.Empty || grant.AdmitOperationId == Guid.Empty)
        {
            throw new InvalidDataException("OnlineGrantInvalid");
        }

        if (grant.SnapshotDigest is not null)
        {
            StorageGuards.Digest(grant.SnapshotDigest);
        }
    }

    public static byte[] Canonical(OnlineGrant grant, bool processRenewal = false)
    {
        ValidateGrant(grant);
        string domain = "UptimeHarbor.OnlineGrant.v1";
        if (processRenewal)
        {
            if (grant.Purpose != OnlineGrantPurpose.Process)
            {
                throw new InvalidDataException("OnlineProcessRenewalPurposeInvalid");
            }

            domain = "UptimeHarbor.OnlineProcessRenewal.v1";
        }

        return StrictOnboardingJson.Encode(new { Domain = domain, Grant = grant with { Signature = string.Empty } });
    }

    public static Guid ConsumeId(Guid grantId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"UptimeHarbor.OnlineGrant.Consume.v1/{grantId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
