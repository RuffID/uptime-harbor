using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Принятый Bind истёкшего leaf опирается только на точную регистрацию продолжения.</summary>
internal static class VoterContinuationBinding
{
    internal static string? Refusal(RevisionImage image, RevisionCommand command, DateTimeOffset now, long minimumIndex,
        bool replay = false)
    {
        VoterContinuationCommand data = command.VoterContinuation!;
        VoterContinuationContext context = data.Context;
        EnrollmentIssuance leaf = context.Request.Renewal.Issuance;
        AcceptedVoterContinuation? accepted = image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(context.Request.OperationId);
        if (accepted?.Authorization is not { } approval || accepted.Registration is not { } registration)
        {
            return "VoterContinuationRegistrationRequired";
        }

        CommandResult? proposed = RevisionDispatcher.ReadVoterCredentialDecision(image, accepted.Request);
        CommandResult? approved = RevisionDispatcher.ReadVoterCredentialDecision(image, approval);
        CommandResult? registered = RevisionDispatcher.ReadVoterCredentialDecision(image, registration);
        if (proposed is not { Outcome: OperationOutcome.Committed } || approved is not { Outcome: OperationOutcome.Committed }
            || registered is not { Outcome: OperationOutcome.Committed } || proposed.Index >= approved.Index
            || approved.Index >= registered.Index || registered.Index > minimumIndex
            || proposed.Term != context.Term || approved.Term != context.Term || registered.Term != context.Term)
        {
            return "VoterContinuationRegistrationChanged";
        }

        if (accepted.Request.VoterContinuation!.Context != context || approval.VoterContinuation!.Context != context
            || registration.VoterContinuation!.Context != context || approval.VoterContinuation.PriorDigest != proposed.Digest
            || registration.VoterContinuation.PriorDigest != approved.Digest || data.PriorDigest != registered.Digest
            || now < registration.VoterContinuation.Acceptance!.AtUtc)
        {
            return "VoterContinuationRegistrationChanged";
        }

        if (command.ActorUserId != approval.ActorUserId || command.ActorRevision != approval.ActorRevision
            || registration.ActorUserId != approval.ActorUserId || registration.ActorRevision != approval.ActorRevision
            || now < approval.CreatedAtUtc || now - approval.CreatedAtUtc >= TimeSpan.FromMinutes(5)
            || !OnboardingReducer.Active(image, approval.ActorUserId, approval.ActorRevision))
        {
            return "VoterContinuationAuthorizationExpiredOrChanged";
        }

        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts!.Issuers?.GetValueOrDefault(leaf.IssuerEpoch);
        if (issuer is null || issuer.NodeId != context.AudienceNodeId || issuer.StorageGeneration != context.AudienceStorage
            || issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE || now < leaf.NotBeforeUtc)
        {
            return "VoterContinuationLocalIssuerRequired";
        }

        RevisionImage proofImage = image;
        if (replay)
        {
            if (accepted.Bind != command || !IsCurrentBinding(image, command))
            {
                return "VoterContinuationBindingChanged";
            }

            proofImage = BeforeBinding(image, leaf.IssueId);
        }

        string? refusal = VoterContinuationReducer.Refusal(proofImage, registration, now, minimumIndex, replay: true);
        if (refusal is not null || replay)
        {
            return refusal;
        }

        if (accepted.Bind is not null || image.OnboardingContracts.VoterCredentialRotations?.ContainsKey(leaf.IssueId) == true
            || image.Onboarding?.Invitations.Values.Any(item => item.Attempt?.IssueId == leaf.IssueId) == true
            || image.Operations.Credentials.Values.Any(item => item.Fingerprint == data.Credential!.Fingerprint))
        {
            return "VoterContinuationBindingUnavailable";
        }

        if (image.OnboardingContracts.VoterCredentialRotations?.Values.Any(rotation =>
            rotation.Renewal.Issuance.SpkiDigest == leaf.SpkiDigest || rotation.Renewal.PreviousSpkiDigest == leaf.SpkiDigest) == true)
        {
            return "VoterContinuationKeyReused";
        }

        if (now < command.CreatedAtUtc || now - command.CreatedAtUtc >= TimeSpan.FromSeconds(60)
            || VoterContinuationReducer.UsedNonce(image, data.Evidence!.Nonce))
        {
            return "VoterContinuationBindEvidenceUnavailable";
        }

        return null;
    }

    internal static bool IsCurrentBinding(RevisionImage image, RevisionCommand bind)
    {
        VoterContinuationCommand data = bind.VoterContinuation!;
        EnrollmentIssuance leaf = data.Context.Request.Renewal.Issuance;
        OperationalCredential? previous = image.Operations.Credentials.GetValueOrDefault(data.Context.Request.Renewal.PreviousCredentialId);
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, leaf.NodeId);
        CommandResult? decision = RevisionDispatcher.ReadVoterCredentialDecision(image, bind);
        return decision is { Outcome: OperationOutcome.Committed } && decision.Term == data.Context.Term
            && latest?.BindOperationId == bind.OperationId && latest.BindDigest == decision.Digest
            && latest.CredentialRevision == decision.Index && latest.ContinuationRequestId == data.Context.Request.OperationId
            && latest.Renewal == data.Context.Request.Renewal && !latest.RetiredIndex.HasValue
            && latest.BoundAtUtc == data.Acceptance!.AtUtc && previous is not null && latest.OverlapUntilUtc == previous.ExpiresAtUtc
            && image.Operations.Credentials.GetValueOrDefault(leaf.IssueId) == data.Credential;
    }

    internal static RevisionImage BeforeBinding(RevisionImage image, Guid issueId)
    {
        OnboardingContractImage contracts = image.OnboardingContracts!;
        return image with
        {
            Operations = image.Operations with { Credentials = image.Operations.Credentials.Remove(issueId) },
            OnboardingContracts = contracts with { VoterCredentialRotations = contracts.VoterCredentialRotations!.Remove(issueId) }
        };
    }

    internal static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        VoterContinuationCommand data = command.VoterContinuation!;
        VoterCredentialRequest request = data.Context.Request;
        VoterCredentialRenewal renewal = request.Renewal;
        OperationalCredential credential = data.Credential!;
        OnboardingContractImage contracts = image.OnboardingContracts!;
        ImmutableDictionary<Guid, AcceptedVoterContinuation> continuations = contracts.VoterContinuations!;
        ImmutableDictionary<Guid, VoterCredentialRotation> rotations = contracts.VoterCredentialRotations
            ?? ImmutableDictionary<Guid, VoterCredentialRotation>.Empty;
        AcceptedVoterContinuation accepted = continuations[request.OperationId];
        RevisionCommand registration = accepted.Registration!;
        CommandResult decision = image.Results[registration.OperationId];
        DateTimeOffset boundAt = data.Acceptance!.AtUtc;
        DateTimeOffset expiredAt = image.Operations.Credentials[renewal.PreviousCredentialId].ExpiresAtUtc;
        VoterCredentialRotation bound = new(renewal, registration.OperationId, decision.Index, decision.Digest,
            registration.VoterContinuation!.Acceptance!.AtUtc)
        {
            BindOperationId = command.OperationId, BindDigest = RevisionCodec.Digest(command), CredentialRevision = image.Index,
            BoundAtUtc = boundAt, OverlapUntilUtc = expiredAt, ContinuationRequestId = request.OperationId
        };

        return image with
        {
            OnboardingContracts = contracts with
            {
                VoterContinuations = continuations.SetItem(request.OperationId, accepted with { Bind = command }),
                VoterCredentialRotations = rotations.Add(credential.Id, bound)
            },
            Operations = image.Operations with
            {
                Credentials = image.Operations.Credentials.Add(credential.Id, credential), Revision = image.Index
            }
        };
    }
}
