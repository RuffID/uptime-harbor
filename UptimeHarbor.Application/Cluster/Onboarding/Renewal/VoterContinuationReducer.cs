using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

internal static class VoterContinuationReducer
{
    internal static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        VoterContinuationCommand data = command.VoterContinuation!;
        VoterContinuationAcceptance stamp = data.Acceptance ?? throw new InvalidDataException("VoterContinuationAcceptanceMissing");
        if (stamp.Term != image.Term || stamp.MinimumIndex >= image.Index)
        {
            throw new InvalidDataException("VoterContinuationAcceptanceUnconfirmed");
        }

        bool binding = command.Action == VoterContinuationCommands.BIND;
        string? reason;
        if (binding)
        {
            reason = VoterContinuationBinding.Refusal(image, command, stamp.AtUtc, stamp.MinimumIndex);
        }
        else
        {
            reason = Refusal(image, command, stamp.AtUtc, stamp.MinimumIndex);
        }

        if (reason is null)
        {
            if (binding)
            {
                return Decide(VoterContinuationBinding.Apply(image, command), command, null);
            }

            OnboardingContractImage contracts = image.OnboardingContracts!;
            ImmutableDictionary<Guid, AcceptedVoterContinuation> requests = contracts.VoterContinuations
                ?? ImmutableDictionary<Guid, AcceptedVoterContinuation>.Empty;
            Guid requestId = data.Context.Request.OperationId;
            AcceptedVoterContinuation updated = command.Action switch
            {
                VoterContinuationCommands.REQUEST => new(command),
                VoterContinuationCommands.AUTHORIZE => requests[requestId] with { Authorization = command },
                VoterContinuationCommands.REGISTER => requests[requestId] with { Registration = command },
                _ => throw new InvalidDataException("VoterContinuationActionInvalid")
            };
            contracts = contracts with { VoterContinuations = requests.SetItem(requestId, updated) };
            if (command.Action == VoterContinuationCommands.REGISTER)
            {
                OnlineProcessLease process = Lease(command, image.Index);
                var processes = contracts.OnlineProcesses ?? ImmutableDictionary<Guid, OnlineProcessLease>.Empty;
                contracts = contracts with { OnlineProcesses = processes.SetItem(command.EntityId, process) };
            }

            image = image with { OnboardingContracts = contracts };
        }

        return Decide(image, command, reason);
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? reason)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (reason is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult decision = new(command.OperationId, RevisionCodec.Digest(command), outcome, image.Index, image.Term,
            image.Index, reason, null, null, command.ActorRevision);
        return image with { Results = image.Results.Add(decision.OperationId, decision) };
    }

    internal static string? Refusal(RevisionImage image, RevisionCommand command, DateTimeOffset now, long minimumIndex,
        bool replay = false)
    {
        VoterContinuationCommand data = command.VoterContinuation!;
        VoterContinuationContext context = data.Context;
        RevisionImage identityImage = image;
        if (context.BoundSource is { } source)
        {
            if (!VoterContinuationBoundRecovery.TryResolve(image, context, out RevisionCommand? originalBind)
                || source.CredentialRevision > minimumIndex || !VoterContinuationBinding.IsCurrentBinding(image, originalBind))
            {
                return "VoterContinuationBoundSourceChanged";
            }

            identityImage = VoterContinuationBinding.BeforeBinding(image, context.Request.Renewal.Issuance.IssueId);
        }

        if (!IdentityCurrent(identityImage, context, now))
        {
            return "VoterContinuationIdentityChanged";
        }

        bool actorRequired = command.Action != VoterContinuationCommands.REQUEST;
        if (actorRequired && !OnboardingReducer.Active(image, command.ActorUserId, command.ActorRevision))
        {
            return "VoterContinuationAdministratorChanged";
        }

        var requests = image.OnboardingContracts!.VoterContinuations;
        AcceptedVoterContinuation? accepted = requests?.GetValueOrDefault(context.Request.OperationId);
        OnlineProcessLease? current = image.OnboardingContracts.OnlineProcesses?.GetValueOrDefault(command.EntityId);
        OnlineProcessLease? registered = null;
        if (replay && accepted?.Registration is { } registration)
        {
            registered = Lease(registration, image.Results[registration.OperationId].Index);
        }

        bool processCurrent = current == context.Predecessor && ProcessCandidate(current, context.Process, now);
        if (registered is not null)
        {
            processCurrent = current == registered && now < registered.LeaseUntilUtc;
        }

        if (!processCurrent || context.Predecessor?.Index > minimumIndex
            || registered is null && current?.BootId != context.Process.BootId && UsedBoot(image, context))
        {
            return "VoterContinuationProcessChanged";
        }

        if (replay)
        {
            return null;
        }

        TimeSpan age = now - command.CreatedAtUtc;
        TimeSpan maxAge = TimeSpan.FromSeconds(60);
        if (command.Action == VoterContinuationCommands.AUTHORIZE)
        {
            maxAge = TimeSpan.FromMinutes(5);
        }

        if (age < TimeSpan.Zero || age >= maxAge)
        {
            return "VoterContinuationEvidenceExpired";
        }

        if (data.Evidence is { } evidence && UsedNonce(image, evidence.Nonce))
        {
            return "VoterContinuationNonceReused";
        }

        if (command.Action == VoterContinuationCommands.REQUEST)
        {
            if (current is { CredentialContinuation: true } && current.BootId == context.Process.BootId)
            {
                return "VoterContinuationProcessAlreadyRegistered";
            }

            Guid issueId = context.Request.Renewal.Issuance.IssueId;
            if (context.BoundSource is null
                && (requests?.Values.Any(item => item.Request.VoterContinuation!.Context.Request.Renewal.Issuance.IssueId == issueId) == true
                || image.OnboardingContracts.VoterCredentialRotations?.ContainsKey(issueId) == true
                || image.Onboarding?.Invitations.Values.Any(item => item.Attempt?.IssueId == issueId) == true))
            {
                return "VoterContinuationIssueAlreadyReserved";
            }

            return accepted is null ? null : "VoterContinuationRequestAlreadyAccepted";
        }

        RevisionCommand? prior = accepted?.Authorization;
        if (command.Action == VoterContinuationCommands.AUTHORIZE)
        {
            prior = accepted?.Request;
        }

        if (prior is null || prior.VoterContinuation!.Context != context || data.PriorDigest != RevisionCodec.Digest(prior)
            || !image.Results.TryGetValue(prior.OperationId, out CommandResult? decision)
            || decision.Outcome != OperationOutcome.Committed || decision.Digest != data.PriorDigest || decision.Index > minimumIndex
            || decision.Term != context.Term || now < prior.VoterContinuation.Acceptance!.AtUtc)
        {
            return "VoterContinuationPriorDecisionChanged";
        }

        if (command.Action == VoterContinuationCommands.REGISTER
            && (command.ActorUserId != prior.ActorUserId || command.ActorRevision != prior.ActorRevision
                || now - prior.CreatedAtUtc >= TimeSpan.FromMinutes(5)))
        {
            return "VoterContinuationAuthorizationExpiredOrChanged";
        }

        return null;
    }

    internal static bool UsedNonce(RevisionImage image, string nonce)
    {
        return image.OnboardingContracts?.VoterContinuations?.Values.Any(item =>
            item.Request.VoterContinuation!.Evidence!.Nonce == nonce
            || item.Registration?.VoterContinuation!.Evidence!.Nonce == nonce
            || item.Bind?.VoterContinuation!.Evidence!.Nonce == nonce) == true;
    }

    internal static bool ProcessCandidate(OnlineProcessLease? previous, VoterContinuationProcess process, DateTimeOffset now)
    {
        if (previous is null)
        {
            return process.Epoch == 1;
        }

        if (previous.BootId == process.BootId && previous.BootChallenge == process.BootChallenge)
        {
            return process.Epoch == previous.ProcessEpoch && process.RecoveryVersion == previous.RecoveryVersion
                && now < previous.LeaseUntilUtc;
        }

        return previous.BootId != process.BootId && previous.BootChallenge != process.BootChallenge
            && previous.ProcessEpoch < long.MaxValue && process.Epoch == previous.ProcessEpoch + 1 && now >= previous.LeaseUntilUtc;
    }

    private static bool UsedBoot(RevisionImage image, VoterContinuationContext context)
    {
        Guid node = context.Request.Renewal.Issuance.NodeId;
        VoterContinuationProcess process = context.Process;
        OnboardingContractImage contracts = image.OnboardingContracts!;
        bool ordinary = contracts.OnlineGrants?.Values.Any(item => item.Grant.Purpose == OnlineGrantPurpose.Process
            && item.Grant.NodeId == node && (item.Grant.BootId == process.BootId || item.Grant.BootChallenge == process.BootChallenge)) == true;
        return ordinary || contracts.VoterContinuations?.Values.Any(item => item.Registration is { } registration
            && registration.EntityId == node && Matches(registration.VoterContinuation!.Context.Process)) == true;

        bool Matches(VoterContinuationProcess historical)
        {
            return historical.BootId == process.BootId || historical.BootChallenge == process.BootChallenge;
        }
    }

    internal static OnlineProcessLease Lease(RevisionCommand registration, long index)
    {
        VoterContinuationCommand data = registration.VoterContinuation!;
        VoterContinuationContext context = data.Context;
        VoterContinuationProcess process = context.Process;
        DateTimeOffset deadline = data.Acceptance!.AtUtc.AddMinutes(2);
        if (context.Predecessor is { } previous && previous.BootId == process.BootId)
        {
            deadline = previous.LeaseUntilUtc;
        }

        return new(registration.EntityId, registration.OperationId, process.BootId, process.BootChallenge,
            process.Epoch, process.RecoveryVersion, deadline, index) { CredentialContinuation = true };
    }

    private static bool IdentityCurrent(RevisionImage image, VoterContinuationContext context, DateTimeOffset now)
    {
        VoterCredentialRenewal renewal = context.Request.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(leaf.NodeId);
        OperationalNode? audience = image.Operations.Nodes.GetValueOrDefault(context.AudienceNodeId);
        OperationalCredential? old = image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId);
        var issuers = image.OnboardingContracts?.Issuers;
        AcceptedEnrollmentIssuer? previousIssuer = issuers?.GetValueOrDefault(context.PreviousIssuerEpoch);
        AcceptedEnrollmentIssuer? issuer = issuers?.GetValueOrDefault(leaf.IssuerEpoch);
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, leaf.NodeId);
        bool exactCut = image.ClusterId == context.Request.ClusterId && image.Generation == context.Request.Generation
            && image.Operations.MembershipVersion == context.MembershipVersion && image.Operations.MaintenanceId == Guid.Empty
            && !image.Operations.Changes.Values.Any(change => !change.ConfirmedVersion.HasValue);
        bool voters = node is { Voting: true, Removed: false } && node.StorageGeneration == leaf.StorageGeneration && node.Address == leaf.PeerUri
            && audience is { Voting: true, Activated: true, Removed: false } && audience.StorageGeneration == context.AudienceStorage;
        if (!exactCut || !voters || old is null)
        {
            return false;
        }

        bool previousLeaf = old.RevokedAtUtc is null && old.NodeId == leaf.NodeId && old.Fingerprint == renewal.PreviousFingerprint
            && (!old.AdmissionId.HasValue || old.ConsumedAtUtc.HasValue) && now >= old.ExpiresAtUtc
            && previousIssuer is { RevokedAtUtc: null } && previousIssuer.Revision == context.PreviousIssuerRevision
            && (latest is null || latest.Renewal.Issuance.IssueId == old.Id
                && latest.Renewal.Issuance.SpkiDigest == renewal.PreviousSpkiDigest)
            && (latest?.CredentialRevision ?? 0) == renewal.PreviousCredentialRevision;
        bool issuing = issuer is { RevokedAtUtc: null, RetiredAtUtc: null } && issuer.Revision == context.IssuerRevision
            && now >= issuer.NotBeforeUtc && now < issuer.NotAfterUtc && now < leaf.NotAfterUtc
            && leaf.NotBeforeUtc >= issuer.NotBeforeUtc && leaf.NotAfterUtc <= issuer.NotAfterUtc
            && image.Operations.Nodes.GetValueOrDefault(issuer.NodeId) is { Voting: true, Removed: false } owner
            && owner.StorageGeneration == issuer.StorageGeneration;
        return previousLeaf && issuing && !image.Operations.Credentials.ContainsKey(leaf.IssueId);
    }
}
