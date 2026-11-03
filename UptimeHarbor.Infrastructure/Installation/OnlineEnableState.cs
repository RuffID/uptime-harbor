using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Не восстанавливает старый grant из SQL. Публикация разрешена после fresh barrier и accepted consumption.</summary>
public class OnlineEnableState(RevisionStateStore revisions, InstallationState installation,
    InstallationLease lease, NodeReadiness readiness, TimeProvider time)
{
    private OnlineGrant? current;

    public bool Confirm(OnlineGrant grant)
    {
        if (!Matches(grant))
        {
            return false;
        }

        Volatile.Write(ref current, grant);
        return true;
    }

    public bool Allows()
    {
        OnlineGrant? grant = Volatile.Read(ref current);
        return grant is not null && Matches(grant);
    }

    public long AcceptedIndex
    {
        get
        {
            OnlineGrant? grant = Current;
            if (grant is null)
            {
                return 0;
            }

            return revisions.Current!.OnboardingContracts!.OnlineGrants![grant.GrantId].ConsumedIndex!.Value;
        }
    }

    public OnlineGrant? Current
    {
        get
        {
            if (Allows())
            {
                return Volatile.Read(ref current);
            }

            return null;
        }
    }

    private bool Matches(OnlineGrant grant)
    {
        LocalInstallationProfile? profile = installation.Current;
        RevisionImage? image = revisions.Current;
        AcceptedOnlineGrant? accepted = image?.OnboardingContracts?.OnlineGrants?.GetValueOrDefault(grant.GrantId);
        CommandResult? result = image?.Results.GetValueOrDefault(OnlineLifecycleCommands.ConsumeId(grant.GrantId));
        return profile is not null && profile.BootId == lease.BootId && profile.AttemptId == grant.AttemptId
            && profile.ClusterId == grant.ClusterId && profile.ClusterGeneration == grant.Generation
            && profile.CredentialId == grant.CredentialId
            && profile.NodeId == grant.NodeId && profile.StorageGeneration == grant.StorageGeneration
            && grant.Purpose == OnlineGrantPurpose.Enable && grant.BootId == lease.BootId
            && grant.BootChallenge == lease.BootChallenge && grant.RecoveryVersion == readiness.RecoveryVersion
            && revisions.Available && image is not null && accepted?.Grant == grant
            && accepted.ConsumedIndex.HasValue && result?.Outcome == UptimeHarbor.Domain.Cluster.OperationOutcome.Committed
            && result.Index == accepted.ConsumedIndex && result.Digest == accepted.ConsumedDigest
            && OnlineGrantPolicy.Current(image, grant, time.GetUtcNow(), consumed: true);
    }
}
