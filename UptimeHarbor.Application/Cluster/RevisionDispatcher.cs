using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Application.Cluster;

public class RevisionDispatcher(IConsensusWriter consensus, RevisionStateStore accepted, IClusterStateView? view = null,
    IMonitoringAudit? monitoringAudit = null, TimeProvider? time = null, IDeliveryAudit? deliveryAudit = null,
    IOnboardingAcceptance? onboarding = null, IOnboardingAudit? onboardingAudit = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    internal Task<CommandResult> AcceptVoterCredentialAsync(RevisionCommand command,
        Func<CancellationToken, Task<(RevisionImage Image, long Recovery, ConsensusStatus Leader, DateTimeOffset AtUtc)>> confirm,
        CancellationToken token)
    {
        if (!Onboarding.Renewal.VoterCredentialCommands.IsRotation(command))
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionScopeInvalid");
        }

        return AcceptCredentialAsync(async cancellationToken =>
        {
            var (image, recovery, leader, now) = await confirm(cancellationToken);
            RevisionCommand stamped = command with
            {
                EnrollmentTrust = command.EnrollmentTrust! with { CoordinatorAtUtc = now, CoordinatorTerm = leader.Term }
            };
            return (stamped, image, recovery, leader);
        }, null, token);
    }

    internal Task<CommandResult> AcceptVoterContinuationAsync(
        Func<CancellationToken, Task<(RevisionCommand Command, RevisionImage Image, long Recovery, ConsensusStatus Leader)>> confirm,
        Action<RevisionImage, RevisionCommand> verifyResult, CancellationToken token)
    {
        return AcceptCredentialAsync(async cancellationToken =>
        {
            var acceptance = await confirm(cancellationToken);
            if (!VoterContinuationCommands.IsContinuation(acceptance.Command) || acceptance.Command.VoterContinuation?.Acceptance is null)
            {
                throw new UnauthorizedAccessException("VoterContinuationAdmissionScopeInvalid");
            }

            return acceptance;
        }, verifyResult, token);
    }

    private async Task<CommandResult> AcceptCredentialAsync(
        Func<CancellationToken, Task<(RevisionCommand Command, RevisionImage Image, long Recovery, ConsensusStatus Leader)>> confirm,
        Action<RevisionImage, RevisionCommand>? verifyResult, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            var (command, image, recovery, leader) = await confirm(token);
            using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
                leader.ConsensusToken, leader.LeadershipToken);
            CommandResult? previous = ReadVoterCredentialDecision(image, command);
            if (previous is not null)
            {
                verifyResult?.Invoke(image, command);
                RequireCurrent();
                return previous;
            }

            byte[] payload = RevisionCodec.Encode(command);
            RequireCurrent();
            await consensus.ReplicateAsync(payload, RevisionCodec.COMMAND_ID, guard.Token);
            guard.Token.ThrowIfCancellationRequested();
            RevisionImage applied = accepted.Current ?? throw new InvalidOperationException("DecisionUnavailable");
            if (!accepted.TryAtCurrent(applied, recovery, static () => { }))
            {
                throw new InvalidOperationException("DecisionUnavailable");
            }

            CommandResult result = ReadVoterCredentialDecision(applied, command)
                ?? throw new InvalidOperationException("DecisionUnavailable");
            if (verifyResult is null)
            {
                onboardingAudit?.Accepted(image, applied, command, result);
            }
            else
            {
                if (command.Action == VoterContinuationCommands.BIND)
                {
                    onboardingAudit?.Accepted(image, applied, command, result);
                }

                verifyResult(applied, command);
                guard.Token.ThrowIfCancellationRequested();
                RuntimeGenerationContext.Verify();
                if (!accepted.TryAtCurrent(applied, recovery, static () => { }))
                {
                    throw new InvalidOperationException("DecisionUnavailable");
                }
            }
            return result;

            void RequireCurrent()
            {
                guard.Token.ThrowIfCancellationRequested();
                RuntimeGenerationContext.Verify();
                if (!accepted.TryAtCurrent(image, recovery, static () => { }))
                {
                    throw new UnauthorizedAccessException("VoterCredentialAdmissionProjectionChanged");
                }
            }
        }
        finally
        {
            serial.Release();
        }
    }

    internal static CommandResult? ReadVoterCredentialDecision(RevisionImage image, RevisionCommand command)
    {
        if (!image.Results.TryGetValue(command.OperationId, out CommandResult? result))
        {
            return null;
        }

        if (result.OperationId != command.OperationId || result.Digest != RevisionCodec.Digest(command)
            || result.Index <= 0 || result.Index > image.Index || result.AppliedRevision != result.Index
            || result.Term <= 0 || result.Term > image.Term
            || result.Outcome is not (Domain.Cluster.OperationOutcome.Committed or Domain.Cluster.OperationOutcome.Rejected))
        {
            throw new InvalidDataException("VoterCredentialAdmissionDecisionMismatch");
        }

        return result;
    }

    public async Task<CommandResult> AcceptAsync(RevisionCommand command, Guid trustedOriginNodeId,
        CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            return await AcceptCoreAsync(command, trustedOriginNodeId, cancellationToken);
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task<CommandResult> AcceptCoreAsync(RevisionCommand command, Guid trustedOriginNodeId,
        CancellationToken cancellationToken)
    {
        if (command.OriginNodeId != trustedOriginNodeId || !accepted.Available)
        {
            throw new UnauthorizedAccessException("UnconfirmedOriginOrProjection");
        }

        byte[] payload = RevisionCodec.Encode(command);
        RevisionImage image = accepted.Current!;
        if (command.ClusterId != image.ClusterId || command.ClusterGeneration != image.Generation)
        {
            throw new InvalidDataException("ClusterMismatch");
        }

        if (ClusterOperationValidation.IsInternal(command))
        {
            ConsensusStatus? status = view?.Consensus;
            bool leader = status is not null && status.IsLeader && status.NodeId == trustedOriginNodeId
                && !status.LeadershipToken.IsCancellationRequested && !status.ConsensusToken.IsCancellationRequested;
            bool recovered = view?.Accepted.CaughtUp == true && view.Accepted.SnapshotConfirmed;
            if (!leader || !recovered || command.Operation!.MembershipVersion != status!.MembershipVersion
                || command.Operation.CoordinatorTerm != status.Term)
            {
                throw new UnauthorizedAccessException("LifecycleLeaderUnconfirmed");
            }
        }

        if (DeliveryCommands.IsSystem(command))
        {
            DeliveryCommand delivery = command.Delivery!;
            if (!image.Monitoring.Nodes.TryGetValue(trustedOriginNodeId, out Guid storage)
                || storage != delivery.StorageGeneration)
            {
                throw new UnauthorizedAccessException("SenderNotAdmitted");
            }

            if (command.SystemSource == "DeliveryCoordinator")
            {
                ConsensusStatus? status = view?.Consensus;
                SenderAuthorityKey? key = delivery.Authority;
                bool coordinator = status is not null && status.IsLeader && status.NodeId == trustedOriginNodeId
                    && status.Term == key?.Term && status.MembershipVersion == key.MembershipVersion
                    && view!.Accepted.SenderEpoch == key.SenderEpoch && view.Accepted.CaughtUp
                    && view.Accepted.DeliveryStateConfirmed && view.Accepted.Activated && view.Accepted.ManuallyEnabled
                    && !status.LeadershipToken.IsCancellationRequested && !status.ConsensusToken.IsCancellationRequested;
                if (!coordinator)
                {
                    throw new UnauthorizedAccessException("SenderNotConfirmed");
                }
            }
        }

        if (MonitoringCommandValidation.IsSystem(command))
        {
            MonitoringCommand monitoring = command.Monitoring!;
            if (!image.Monitoring.Nodes.TryGetValue(trustedOriginNodeId, out Guid storage)
                || storage != monitoring.StorageGeneration)
            {
                throw new UnauthorizedAccessException("ObserverNotAdmitted");
            }

            if (command.Action != "RecordObservations")
            {
                ConsensusStatus? status = view?.Consensus;
                bool coordinator = status is not null && status.IsLeader && status.NodeId == trustedOriginNodeId
                    && status.Term == monitoring.CoordinatorTerm && !status.LeadershipToken.IsCancellationRequested
                    && !status.ConsensusToken.IsCancellationRequested && view!.Accepted.CaughtUp;
                if (!coordinator)
                {
                    throw new UnauthorizedAccessException("CoordinatorNotConfirmed");
                }
            }
            else if (monitoring.Observations.Any(item => item.CheckedAtUtc > (time ?? TimeProvider.System).GetUtcNow().AddSeconds(5)))
            {
                throw new InvalidDataException("ObservationClockAhead");
            }
        }

        // The generic dispatcher cannot authenticate renewal evidence, including an exact replay.
        if (VoterContinuationCommands.IsContinuation(command))
        {
            throw new UnauthorizedAccessException("VoterContinuationTypedAuthorityRequired");
        }

        if (Onboarding.Renewal.VoterCredentialCommands.IsRotation(command))
        {
            throw new InvalidOperationException("VoterCredentialRenewalAuthorityUnavailable");
        }

        if (image.Results.TryGetValue(command.OperationId, out CommandResult? previous))
        {
            if (previous.Digest != RevisionCodec.Digest(command))
            {
                throw new InvalidDataException("IdempotencyKeyReuse");
            }

            return previous;
        }

        RevisionImage before = image;
        if (OnboardingCommands.IsOnboarding(command) || EnrollmentTrustCommands.IsTrust(command) || OnlineLifecycleCommands.IsOnline(command))
        {
            if (onboarding is null)
            {
                throw new InvalidOperationException("OnboardingUnavailable");
            }

            command = await onboarding.PrepareAsync(command, cancellationToken);
            payload = RevisionCodec.Encode(command);
        }

        await consensus.ReplicateAsync(payload, RevisionCodec.COMMAND_ID, cancellationToken);
        image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        if (!accepted.Available || !image.Results.TryGetValue(command.OperationId, out CommandResult? result))
        {
            throw new InvalidOperationException("DecisionUnavailable");
        }

        if (OnboardingCommands.IsOnboarding(command) || EnrollmentTrustCommands.IsTrust(command) || OnlineLifecycleCommands.IsOnline(command))
        {
            onboardingAudit?.Accepted(before, image, command, result);
        }

        foreach (MonitoringEvent acceptedEvent in image.Monitoring.Events.Values.Where(item => item.Index == result.Index
            && !before.Monitoring.Events.ContainsKey(item.Id)))
        {
            MonitoringIncident incident = image.Monitoring.Incidents[acceptedEvent.IncidentId];
            monitoringAudit?.Transition(command.OriginNodeId, incident.Id, acceptedEvent.Id, incident.TargetId, result.Index);
        }

        foreach (AcceptedDeliveryAttempt attempt in image.Monitoring.Attempts.Values.Where(item => item.Index == result.Index
            && before.Monitoring.Attempts.GetValueOrDefault(item.Id) != item))
        {
            MonitoringIntent intent = image.Monitoring.Intents[attempt.IntentId];
            deliveryAudit?.Accepted(attempt.Authority.LeaderNodeId, intent.IncidentId, intent.Id, attempt.Id,
                attempt.Term, attempt.Outcome.ToString(), attempt.FailureKind?.Split('/')[0]);
        }

        return result;
    }
}
