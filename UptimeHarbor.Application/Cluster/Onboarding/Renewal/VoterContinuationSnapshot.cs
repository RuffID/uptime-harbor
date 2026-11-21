using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

internal static class VoterContinuationSnapshot
{
    internal static void Validate(RevisionImage image)
    {
        var requests = image.OnboardingContracts?.VoterContinuations;
        if (requests is null)
        {
            return;
        }

        HashSet<string> nonces = new(StringComparer.Ordinal);
        HashSet<Guid> issueIds = [];
        Dictionary<Guid, long> registrations = [];
        foreach ((Guid id, AcceptedVoterContinuation accepted) in requests)
        {
            RevisionCommand request = accepted.Request;
            CommandResult proposed = RequireDecision(image, request, VoterContinuationCommands.REQUEST, null);
            VoterContinuationContext context = request.VoterContinuation!.Context;
            EnrollmentIssuance leaf = context.Request.Renewal.Issuance;
            bool exactIssuance;
            if (context.BoundSource is { } source)
            {
                exactIssuance = source.CredentialRevision <= request.VoterContinuation.Acceptance!.MinimumIndex
                    && VoterContinuationBoundRecovery.TryResolve(image, context, out _);
            }
            else
            {
                exactIssuance = issueIds.Add(leaf.IssueId);
            }

            if (id != context.Request.OperationId || !exactIssuance || !nonces.Add(request.VoterContinuation.Evidence!.Nonce)
                || image.Operations.Nodes.GetValueOrDefault(leaf.NodeId)?.StorageGeneration != leaf.StorageGeneration
                || image.Operations.Nodes.GetValueOrDefault(context.AudienceNodeId)?.StorageGeneration != context.AudienceStorage
                || image.Operations.Credentials.GetValueOrDefault(context.Request.Renewal.PreviousCredentialId) is not { } previous
                || previous.NodeId != leaf.NodeId || previous.Fingerprint != context.Request.Renewal.PreviousFingerprint
                || !HasIssuer(context.PreviousIssuerEpoch, context.PreviousIssuerRevision) || !HasIssuer(leaf.IssuerEpoch, context.IssuerRevision))
            {
                throw new InvalidDataException("VoterContinuationSnapshotContextInvalid");
            }

            if (context.Predecessor is { } predecessor)
            {
                if (predecessor.Index > request.VoterContinuation.Acceptance!.MinimumIndex)
                {
                    throw new InvalidDataException("VoterContinuationSnapshotPredecessorInvalid");
                }

                OnlineLifecycleSnapshot.ValidateLease(image, predecessor.NodeId, predecessor);
            }

            if (accepted.Authorization is not { } authorization)
            {
                if (accepted.Registration is not null || accepted.Bind is not null)
                {
                    throw new InvalidDataException("VoterContinuationSnapshotAuthorizationMissing");
                }

                continue;
            }

            RequireDecision(image, authorization, VoterContinuationCommands.AUTHORIZE, request);
            if (accepted.Registration is { } registration)
            {
                CommandResult registered = RequireDecision(image, registration, VoterContinuationCommands.REGISTER, authorization);
                if (registration.ActorUserId != authorization.ActorUserId || registration.ActorRevision != authorization.ActorRevision
                    || registration.VoterContinuation!.Acceptance!.AtUtc - authorization.CreatedAtUtc >= TimeSpan.FromMinutes(5)
                    || !nonces.Add(registration.VoterContinuation.Evidence!.Nonce) || registered.Index <= proposed.Index)
                {
                    throw new InvalidDataException("VoterContinuationSnapshotRegistrationInvalid");
                }

                registrations[leaf.NodeId] = Math.Max(registered.Index, registrations.GetValueOrDefault(leaf.NodeId));
                if (accepted.Bind is { } bind)
                {
                    ValidateBinding(image, bind, authorization, registration, registered, nonces);
                }
            }
            else if (accepted.Bind is not null)
            {
                throw new InvalidDataException("VoterContinuationSnapshotRegistrationMissing");
            }
        }

        foreach ((Guid node, long registered) in registrations)
        {
            long ordinary = (image.OnboardingContracts!.OnlineGrants?.Values ?? [])
                .Where(grant => grant.Grant.NodeId == node && grant.Grant.Purpose == OnlineGrantPurpose.Process)
                .Select(grant => grant.AuthorizedIndex).DefaultIfEmpty().Max();
            if (image.OnboardingContracts.OnlineProcesses?.GetValueOrDefault(node)?.Index != Math.Max(ordinary, registered))
            {
                throw new InvalidDataException("VoterContinuationSnapshotProcessMissing");
            }
        }

        bool HasIssuer(Guid epoch, long revision)
        {
            return image.OnboardingContracts!.Issuers?.GetValueOrDefault(epoch)?.Revision >= revision;
        }
    }

    private static void ValidateBinding(RevisionImage image, RevisionCommand bind, RevisionCommand approval,
        RevisionCommand registration, CommandResult registered, HashSet<string> nonces)
    {
        CommandResult decision = RequireDecision(image, bind, VoterContinuationCommands.BIND, registration);
        VoterContinuationCommand data = bind.VoterContinuation!;
        DateTimeOffset at = data.Acceptance!.AtUtc;
        Guid issueId = data.Context.Request.Renewal.Issuance.IssueId;
        VoterCredentialRotation? rotation = image.OnboardingContracts!.VoterCredentialRotations?.GetValueOrDefault(issueId);
        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(issueId);
        bool authorization = bind.ActorUserId == approval.ActorUserId && bind.ActorRevision == approval.ActorRevision
            && at - approval.CreatedAtUtc < TimeSpan.FromMinutes(5);
        bool process = at < VoterContinuationReducer.Lease(registration, registered.Index).LeaseUntilUtc;
        bool exactRotation = rotation is not null && rotation.ContinuationRequestId == data.Context.Request.OperationId
            && rotation.Renewal == data.Context.Request.Renewal && rotation.BindOperationId == bind.OperationId
            && rotation.BindDigest == decision.Digest && rotation.CredentialRevision == decision.Index && rotation.BoundAtUtc == at
            && rotation.AuthorizationOperationId == registration.OperationId && rotation.AuthorizationDigest == registered.Digest
            && rotation.AuthorizedIndex == registered.Index && rotation.AuthorizedAtUtc == registration.VoterContinuation!.Acceptance!.AtUtc;
        if (!authorization || !process || !exactRotation || !nonces.Add(data.Evidence!.Nonce)
            || credential is null || credential with { RevokedAtUtc = null } != data.Credential)
        {
            throw new InvalidDataException("VoterContinuationSnapshotBindingInvalid");
        }
    }

    internal static void ValidateProcess(RevisionImage image, Guid nodeId, OnlineProcessLease process)
    {
        RevisionCommand? registration = image.OnboardingContracts?.VoterContinuations?.Values
            .Select(item => item.Registration).SingleOrDefault(item => item?.OperationId == process.GrantId);
        if (registration is null || registration.EntityId != nodeId || registration.VoterContinuation?.Acceptance is null
            || image.Results.GetValueOrDefault(process.GrantId) is not { Outcome: OperationOutcome.Committed } result
            || result.Digest != RevisionCodec.Digest(registration) || process != VoterContinuationReducer.Lease(registration, result.Index))
        {
            throw new InvalidDataException("VoterContinuationSnapshotProcessInvalid");
        }
    }

    private static CommandResult RequireDecision(RevisionImage image, RevisionCommand command, string action, RevisionCommand? prior)
    {
        RevisionCodec.Validate(command);
        VoterContinuationCommand? data = command.VoterContinuation;
        VoterContinuationAcceptance? stamp = data?.Acceptance;
        CommandResult? result = image.Results.GetValueOrDefault(command.OperationId);
        TimeSpan maxAge = TimeSpan.FromSeconds(60);
        if (action == VoterContinuationCommands.AUTHORIZE)
        {
            maxAge = TimeSpan.FromMinutes(5);
        }

        if (command.Action != action || stamp is null || command.ClusterId != image.ClusterId
            || command.ClusterGeneration != image.Generation || stamp.AtUtc < command.CreatedAtUtc || stamp.AtUtc - command.CreatedAtUtc >= maxAge
            || !VoterContinuationReducer.ProcessCandidate(data!.Context.Predecessor, data.Context.Process, stamp.AtUtc)
            || result is not { Outcome: OperationOutcome.Committed } || result.OperationId != command.OperationId
            || result.Digest != RevisionCodec.Digest(command) || result.Term != stamp.Term || result.Term > image.Term
            || result.Index <= stamp.MinimumIndex || result.Index > image.Index || result.AppliedRevision != result.Index
            || result.ActorRevision != command.ActorRevision
            || prior is not null && (data.PriorDigest != RevisionCodec.Digest(prior) || data.Context != prior.VoterContinuation!.Context
                || stamp.MinimumIndex < image.Results[prior.OperationId].Index || stamp.AtUtc < prior.VoterContinuation.Acceptance!.AtUtc))
        {
            throw new InvalidDataException("VoterContinuationSnapshotDecisionInvalid");
        }

        return result;
    }
}
