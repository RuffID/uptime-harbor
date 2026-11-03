using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Долговременные команды этапа 04 не входят в исходный фиксированный набор E0 OperationIds.</summary>
public static class LocalCommandStepValidation
{
    public static void Require(LocalInstallationProfile profile, LocalAttemptStep step, RevisionCommand command, string prefix)
    {
        RevisionCodec.Validate(command);
        bool exact = step.AttemptId == profile.AttemptId && step.OperationId == command.OperationId
            && step.RequestDigest == RevisionCodec.Digest(command) && command.ClusterId == profile.ClusterId
            && command.ClusterGeneration == profile.ClusterGeneration && command.OriginNodeId == profile.NodeId
            && step.ProfileRevision <= profile.ProfileRevision && step.RuntimeGeneration <= profile.RuntimeGeneration;
        bool online = prefix.Length == 0 && OnlineLifecycleCommands.IsOnline(command)
            && command.OnlineLifecycle!.Grant.AttemptId == profile.AttemptId
            && command.OnlineLifecycle.StorageGeneration == profile.StorageGeneration;
        bool enrollment = prefix == "Enrollment/" && OnboardingCommands.NeedsEnrollmentAuthority(command)
            && command.Onboarding?.StorageGeneration == profile.StorageGeneration;
        if (!exact || !online && !enrollment || step.Kind != prefix + command.Action)
        {
            throw new InvalidDataException("LocalCommandStepBindingInvalid");
        }
    }
}
