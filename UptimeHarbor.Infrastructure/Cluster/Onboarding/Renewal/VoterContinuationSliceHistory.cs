using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationSlice;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

internal static class VoterContinuationSliceHistory
{
    internal static void Verify(AcceptedSlice slice, SliceQuery query, RevisionImage local, DateTimeOffset now)
    {
        VoterCredentialRequest request = query.Call.Request;
        BoundHistory history = slice.History ?? throw new InvalidDataException("VoterContinuationSliceHistoryMissing");
        VoterContinuationContext original = history.Requested.Command.VoterContinuation?.Context
            ?? throw new InvalidDataException("VoterContinuationSliceHistoryInvalid");
        VoterContinuationBoundSource source = new(original.Request.OperationId, history.Bound.Decision.Digest,
            history.Bound.Decision.Index);
        if (original.BoundSource is not null || original.Request.Renewal != request.Renewal
            || original.Request.ClusterId != request.ClusterId || original.Request.Generation != request.Generation
            || original.AudienceNodeId != slice.Audience.NodeId || original.AudienceStorage != slice.Audience.StorageGeneration
            || original.Term > slice.Term || original.MembershipVersion > slice.MembershipVersion
            || original.PreviousIssuerEpoch != slice.PreviousIssuer.Epoch || original.PreviousIssuerRevision != slice.PreviousIssuer.Revision
            || original.IssuerRevision != slice.Issuer.Revision || slice.Bound is not null
            || local.Operations.Credentials.TryGetValue(request.Renewal.Issuance.IssueId, out var localLeaf)
                && localLeaf != history.Bound.Command.VoterContinuation?.Credential
            || VoterCredentialPolicy.Latest(local, slice.Subject.NodeId) is { ContinuationRequestId: not null } localBinding
                && (localBinding.ContinuationRequestId != source.RequestId || localBinding.BindDigest != source.BindDigest
                    || localBinding.CredentialRevision != source.CredentialRevision))
        {
            throw new InvalidDataException("VoterContinuationSliceHistoryChanged");
        }

        var historical = slice with { Term = original.Term, RecoveryVersion = original.RecoveryVersion,
            MembershipVersion = original.MembershipVersion, Authorized = history.Authorized };
        Stage[] stages = [history.Requested, history.Authorized, history.Registered, history.Bound];
        for (int index = 0; index < stages.Length; index++)
        {
            string action = Actions[index];
            Stage stage = stages[index];
            Guid id = VoterContinuationCommands.OperationId(action, source.RequestId);
            Stage? previous = null;
            if (index > 0)
            {
                previous = stages[index - 1];
            }

            VerifyStage(stage, action, id, original, previous, historical, now);
            if (local.Results.TryGetValue(id, out var result) && result != stage.Decision)
            {
                throw new InvalidDataException("VoterContinuationSliceHistoryDecisionChanged");
            }
        }

        VerifyLeaf(history.Bound, original.Request, slice.Issuer, now);
        if (query.Discovery is { } pin)
        {
            VoterCredentialProof proof = query.Call.Proof;
            if (original.Request != request || original.Process != new VoterContinuationProcess(proof.BootId,
                proof.BootChallenge, proof.ProcessEpoch, proof.RecoveryVersion)
                || pin.RegistrationDigest != history.Registered.Decision.Digest
                || pin.CertificateDigest != history.Bound.Command.VoterContinuation!.Credential!.Fingerprint
                || slice.Requested is not null || slice.Authorized is not null || slice.Registered is not null)
            {
                throw new InvalidDataException("VoterContinuationSliceDiscoveryChanged");
            }

            VerifyDiscoveredProcess(slice, local, history, source, now);
        }
        else if (request.BoundSource != source || slice.ProcessRegistration is not null
            || original.Process.BootId == query.Call.Proof.BootId || original.Process.BootChallenge == query.Call.Proof.BootChallenge
            || slice.Requested is { } requested && (requested.Decision.Index <= source.CredentialRevision
                || requested.Command.VoterContinuation!.Context.Predecessor is not { CredentialContinuation: true } predecessor
                || predecessor.LeaseUntilUtc > requested.Command.VoterContinuation.Acceptance!.AtUtc
                || !VoterContinuationReducer.ProcessCandidate(predecessor, requested.Command.VoterContinuation.Context.Process,
                    requested.Command.VoterContinuation.Acceptance.AtUtc)))
        {
            throw new InvalidDataException("VoterContinuationSliceRecoverySourceChanged");
        }
    }

    private static void VerifyDiscoveredProcess(AcceptedSlice slice, RevisionImage local, BoundHistory history,
        VoterContinuationBoundSource source, DateTimeOffset now)
    {
        Stage stage = slice.ProcessRegistration ?? throw new InvalidDataException("VoterContinuationSliceProcessMissing");
        RevisionCommand command = stage.Command;
        RevisionCodec.Validate(command);
        if (command.Action != VoterContinuationCommands.REGISTER || command.VoterContinuation is not { Acceptance: { } stamp } data)
        {
            throw new InvalidDataException("VoterContinuationSliceProcessChanged");
        }

        VoterContinuationContext context = data.Context;
        OnlineProcessLease? saved = local.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(slice.Subject.NodeId);
        if (command.EntityId != slice.Subject.NodeId
            || stage.Decision != new CommandResult(command.OperationId, RevisionCodec.Digest(command), OperationOutcome.Committed,
                stage.Decision.Index, context.Term, stage.Decision.Index, ActorRevision: command.ActorRevision)
            || stage.Decision.Index <= 0 || stage.Decision.Index > slice.Head.Index || stage.Decision.Term > slice.Head.Term
            || stamp.AtUtc > now || stamp.AtUtc < command.CreatedAtUtc
            || stamp.Term != context.Term || stamp.RecoveryVersion != context.RecoveryVersion
            || stamp.MembershipVersion != context.MembershipVersion || stamp.MinimumIndex >= stage.Decision.Index
            || slice.Process != VoterContinuationReducer.Lease(command, stage.Decision.Index)
            || stage != history.Registered && (context.BoundSource != source || stage.Decision.Index <= source.CredentialRevision
                || context.Request.Renewal != history.Requested.Command.VoterContinuation!.Context.Request.Renewal)
            || saved is not null && (stage.Decision.Index < saved.Index || context.Process.Epoch < saved.ProcessEpoch
                || stage.Decision.Index == saved.Index && slice.Process != saved)
            || local.Results.TryGetValue(command.OperationId, out var result) && result != stage.Decision)
        {
            throw new InvalidDataException("VoterContinuationSliceProcessChanged");
        }
    }
}
