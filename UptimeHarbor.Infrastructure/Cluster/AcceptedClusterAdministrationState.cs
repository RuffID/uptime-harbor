using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

public class AcceptedClusterAdministrationState(RevisionStateStore state, RevisionDispatcher dispatcher,
    IOptions<ClusterOptions> options, TimeProvider time) : IClusterAdministrationState
{
    public ValueTask<AcceptedMembershipChange?> FindAuthorizedChangeAsync(Guid userId, Guid changeId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RevisionImage? image = state.Current;
        MembershipOperation? change = image?.Operations.Changes.GetValueOrDefault(changeId);
        AcceptedAccount? actor = state.Find(userId);
        if (!state.Available || image is null || change is null || change.ActorUserId != userId
            || !AcceptedAdministratorAccessPolicy.IsActiveAdministrator(actor) || actor!.Revision != change.ActorRevision)
        {
            return ValueTask.FromResult<AcceptedMembershipChange?>(null);
        }

        OperationalNode node = image.Operations.Nodes[change.NodeId];
        if (!change.Remove && OnboardingMembership.Find(image, node.NodeId) is not null)
        {
            // Native Join новой попытки подключается этапом 05 к process-bound протоколу.
            return ValueTask.FromResult<AcceptedMembershipChange?>(null);
        }

        bool trust = change.Remove || image.Operations.Credentials.Values.Any(item => item.NodeId == node.NodeId
            && !item.RevokedAtUtc.HasValue && item.ExpiresAtUtc > time.GetUtcNow()
            && item.AdmissionId.HasValue && !item.ConsumedAtUtc.HasValue && item.AdmissionExpiresAtUtc > change.RequestedAtUtc);
        bool reconciled = image.Operations.Changes.Values.All(item => item.Id == changeId || item.ConfirmedVersion.HasValue);
        AcceptedMembershipChange result = new(change.Id, userId, node.NodeId, new Uri(node.Address), change.Remove,
            change.ExpectedVersion, true, trust, reconciled);
        return ValueTask.FromResult<AcceptedMembershipChange?>(result);
    }

    public async ValueTask<bool> ConfirmProtocolResultAsync(Guid changeId, long membershipVersion, CancellationToken token)
    {
        RevisionImage image = state.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        MembershipOperation change = image.Operations.Changes[changeId];
        if (change.ConfirmedVersion.HasValue)
        {
            return change.ConfirmedVersion.Value == membershipVersion;
        }

        RevisionCommand command = ClusterLifecycleCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            "ConfirmMembership", change.NodeId, new ClusterOperation { ChangeId = changeId, MembershipVersion = membershipVersion }, time.GetUtcNow());
        CommandResult result = await dispatcher.AcceptAsync(command, options.Value.NodeId, token);
        return result.Outcome == OperationOutcome.Committed;
    }
}
