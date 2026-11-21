using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

internal static class VoterContinuationSlice
{
    internal static readonly IReadOnlyList<string> Actions = Array.AsReadOnly(new[]
    {
        VoterContinuationCommands.REQUEST, VoterContinuationCommands.AUTHORIZE,
        VoterContinuationCommands.REGISTER, VoterContinuationCommands.BIND
    });

    internal static ProjectionHead Head(RevisionImage image) =>
        new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);

    internal static string VotersDigest(RevisionImage image) => RevisionCodec.Digest(StrictOnboardingJson.Encode(
        image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed).OrderBy(node => node.NodeId)
            .Select(node => new { node.NodeId, node.StorageGeneration, node.Address, node.RaftMemberId }).ToArray()));

    internal static string ReplayDigest(ProjectionHead replay, long nativeTerm, string votersDigest, BoundDiscovery? discovery = null)
    {
        if (discovery is not null)
        {
            return RevisionCodec.Digest(StrictOnboardingJson.Encode(new
            {
                Replay = replay, NativeTerm = nativeTerm, VotersDigest = votersDigest,
                Discovery = VoterContinuationAuthority.BindChallengeDigest(discovery.RegistrationDigest, discovery.CertificateDigest)
            }));
        }

        return RevisionCodec.Digest(StrictOnboardingJson.Encode(new { Replay = replay, NativeTerm = nativeTerm, VotersDigest = votersDigest }));
    }

    internal static string QueryDigest(SliceQuery query) => RevisionCodec.Digest(StrictOnboardingJson.Encode(query));

    internal static void RequirePrefix(RevisionImage image, ProjectionHead replay)
    {
        RequireAtLeast(Head(image), replay);
        if (image.Index != replay.Index && !HasReplayRecord(image, replay))
        {
            throw new InvalidDataException("VoterContinuationReplayRecordMissing");
        }
    }

    private static bool HasReplayRecord(RevisionImage image, ProjectionHead replay)
    {
        foreach (var continuation in image.OnboardingContracts?.VoterContinuations?.Values ?? [])
        {
            RevisionCommand?[] stages = [continuation.Request, continuation.Authorization, continuation.Registration, continuation.Bind];
            foreach (var stage in stages)
            {
                if (stage is not null
                    && image.Results.GetValueOrDefault(stage.OperationId) is { Outcome: OperationOutcome.Committed } decision
                    && decision.Index == replay.Index && decision.Term == replay.Term
                    && RevisionDispatcher.ReadVoterCredentialDecision(image, stage) == decision)
                {
                    // The WAL head hashes the accepted stamp. Receipts hash the unstamped command.
                    return RevisionCodec.Digest(RevisionCodec.Encode(stage)) == replay.Digest;
                }
            }
        }

        return image.Results.Values.Any(result => result.Index == replay.Index
            && result.Term == replay.Term && result.Digest == replay.Digest);
    }

    internal static void RequireAtLeast(ProjectionHead head, ProjectionHead floor)
    {
        if (head.ClusterId != floor.ClusterId || head.Generation != floor.Generation || floor.Index <= 0 || floor.Term <= 0
            || head.Index < floor.Index || head.Term < floor.Term
            || head.Index == floor.Index && (head.Term != floor.Term || head.Digest != floor.Digest)
            || StorageGuards.Digest(head.Digest!) != head.Digest || StorageGuards.Digest(floor.Digest!) != floor.Digest)
        {
            throw new InvalidDataException("VoterContinuationReplayPrefixChanged");
        }
    }

    internal static void Verify(AcceptedSlice slice, SliceQuery query, RevisionImage local, string audienceAddress,
        AcceptedSlice? prior, DateTimeOffset now)
    {
        VoterCredentialProofRequest call = query.Call;
        VoterCredentialRequest request = call.Request;
        VoterCredentialRenewal renewal = request.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        RequireAtLeast(slice.Head, query.Replay);
        if (slice.QueryDigest != QueryDigest(query) || slice.Term < Math.Max(query.NativeTerm, slice.Head.Term)
            || slice.RecoveryVersion < 0 || slice.MembershipVersion != local.Operations.MembershipVersion
            || slice.MembershipVersion != call.Proof.MembershipVersion || slice.Audience.Address != audienceAddress
            || slice.Audience.NodeId == slice.Subject.NodeId || !slice.Audience.Activated
            || !MatchesVoter(slice.Audience) || !MatchesVoter(slice.Subject)
            || slice.Subject.NodeId != leaf.NodeId || slice.Subject.StorageGeneration != leaf.StorageGeneration
            || slice.Subject.Address != leaf.PeerUri
            || slice.Previous != local.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId)
            || slice.Previous.Id != renewal.PreviousCredentialId || slice.Previous.NodeId != leaf.NodeId
            || slice.Previous.Fingerprint != renewal.PreviousFingerprint || slice.Previous.RevokedAtUtc.HasValue
            || now < slice.Previous.ExpiresAtUtc || slice.PreviousIssuer.RevokedAtUtc.HasValue
            || slice.Issuer.RevokedAtUtc.HasValue || slice.Issuer.RetiredAtUtc.HasValue || slice.Issuer.Epoch != leaf.IssuerEpoch
            || slice.Issuer.NodeId != slice.Audience.NodeId || slice.Issuer.StorageGeneration != slice.Audience.StorageGeneration
            || now < slice.Issuer.NotBeforeUtc || now >= slice.Issuer.NotAfterUtc || now >= leaf.NotAfterUtc
            || !MatchesIssuer(slice.PreviousIssuer) || !MatchesIssuer(slice.Issuer))
        {
            throw new InvalidDataException("VoterContinuationSliceIdentityChanged");
        }

        if (prior is not null)
        {
            RequireAtLeast(slice.Head, prior.Head);
            if (slice.Term != prior.Term || slice.RecoveryVersion != prior.RecoveryVersion || slice.History != prior.History)
            {
                throw new InvalidDataException("VoterContinuationSliceAuthorityChanged");
            }
        }

        bool discovery = query.Discovery is not null;
        if (discovery || request.BoundSource is not null)
        {
            VoterContinuationSliceHistory.Verify(slice, query, local, now);
        }
        else if (slice.History is not null || slice.ProcessRegistration is not null)
        {
            throw new InvalidDataException("VoterContinuationUnexpectedHistory");
        }

        if (discovery)
        {
            return;
        }

        Stage?[] stages = [slice.Requested, slice.Authorized, slice.Registered, slice.Bound];
        Stage?[] priorStages = [prior?.Requested, prior?.Authorized, prior?.Registered, prior?.Bound];
        VoterContinuationContext? context = slice.Requested?.Command.VoterContinuation?.Context;
        Stage? previous = null;
        for (int index = 0; index < stages.Length; index++)
        {
            string action = Actions[index];
            Guid id = VoterContinuationCommands.OperationId(action, request.OperationId);
            CommandResult? localResult = local.Results.GetValueOrDefault(id);
            Stage? stage = stages[index];
            if (localResult is not null && localResult != stage?.Decision
                || priorStages[index] is { } earlier && stage != earlier)
            {
                throw new InvalidDataException("VoterContinuationSliceDecisionChanged");
            }

            if (stage is not null)
            {
                VerifyStage(stage, action, id, context, previous, slice, now);
            }

            previous = stage;
        }

        VoterCredentialProof proof = call.Proof;
        VoterContinuationProcess process = new(proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.RecoveryVersion);
        if (context is not null && (context.Request != request || context.Process != process
            || context.AudienceNodeId != slice.Audience.NodeId || context.AudienceStorage != slice.Audience.StorageGeneration
            || context.Term != slice.Term || context.RecoveryVersion != slice.RecoveryVersion
            || context.MembershipVersion != slice.MembershipVersion || context.PreviousIssuerEpoch != slice.PreviousIssuer.Epoch
            || context.PreviousIssuerRevision != slice.PreviousIssuer.Revision || context.IssuerRevision != slice.Issuer.Revision)
            || slice.Authorized is { } approved && (now < approved.Command.CreatedAtUtc
                || now - approved.Command.CreatedAtUtc >= TimeSpan.FromMinutes(5))
            || (context is not null || request.BoundSource is not null) && !ProcessMatches())
        {
            throw new InvalidDataException("VoterContinuationSliceContextChanged");
        }

        if (request.BoundSource is not null && (slice.Process is not { CredentialContinuation: true }
            || prior?.ProcessRegistration is not null && (context?.Predecessor ?? slice.Process) != prior.Process
                && slice.Process != prior.Process))
        {
            throw new InvalidDataException("VoterContinuationSliceProcessChanged");
        }

        if (slice.Bound is { } bound)
        {
            VerifyLeaf(bound, request, slice.Issuer, now);
        }

        bool MatchesVoter(OperationalNode node) => node is { Voting: true, Removed: false }
            && local.Operations.Nodes.GetValueOrDefault(node.NodeId) is { Voting: true, Removed: false } saved
            && saved.Address == node.Address && saved.StorageGeneration == node.StorageGeneration
            && saved.RaftMemberId == node.RaftMemberId && saved.Revision <= node.Revision && node.Revision <= slice.Head.Index;

        bool MatchesIssuer(AcceptedEnrollmentIssuer issuer) => issuer.Purpose == EnrollmentIssuer.PEER_PURPOSE
            && issuer.Revision > 0 && issuer.Revision <= slice.Head.Index
            && EnrollmentCertificates.Hash(Convert.FromBase64String(issuer.CertificateDer)) == issuer.CertificateDigest
            && (local.OnboardingContracts?.Issuers?.GetValueOrDefault(issuer.Epoch) is not { } saved
                || saved.RevokedAtUtc is null && issuer.Revision >= saved.Revision
                    && issuer.CertificateDigest == saved.CertificateDigest && issuer.NodeId == saved.NodeId
                    && issuer.StorageGeneration == saved.StorageGeneration);

        bool ProcessMatches()
        {
            OnlineProcessLease? saved = local.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(leaf.NodeId);
            if (saved is not null && (slice.Process is null || slice.Process.Index < saved.Index
                || slice.Process.ProcessEpoch < saved.ProcessEpoch || slice.Process.Index == saved.Index && slice.Process != saved))
            {
                return false;
            }

            if (slice.Registered is { } registered)
            {
                return slice.Process == VoterContinuationReducer.Lease(registered.Command, registered.Decision.Index)
                    && now < slice.Process!.LeaseUntilUtc;
            }

            return (context is null || context.Predecessor == slice.Process)
                && VoterContinuationReducer.ProcessCandidate(slice.Process, process, now);
        }
    }

    internal static void VerifyLeaf(Stage bound, VoterCredentialRequest request, AcceptedEnrollmentIssuer issuer, DateTimeOffset now)
    {
        OperationalCredential credential = bound.Command.VoterContinuation!.Credential!;
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        if (credential.RevokedAtUtc.HasValue || !EnrollmentCertificates.ValidateLeaf(certificate, root,
            VoterCredentialProofs.CertificateIdentity(request), request.Renewal.Issuance, credential.Fingerprint, now, server: false))
        {
            throw new InvalidDataException("VoterContinuationSliceBoundLeafInvalid");
        }
    }

    internal static void VerifyStage(Stage stage, string action, Guid id, VoterContinuationContext? context,
        Stage? previous, AcceptedSlice slice, DateTimeOffset now)
    {
        RevisionCommand command = stage.Command;
        RevisionCodec.Validate(command);
        CommandResult decision = stage.Decision;
        if (command.Action != action || command.VoterContinuation is not { Acceptance: { } stamp } data
            || data.Context != context || command.OperationId != id
            || stamp.Term != slice.Term || stamp.RecoveryVersion != slice.RecoveryVersion
            || stamp.MembershipVersion != slice.MembershipVersion || stamp.MinimumIndex >= decision.Index || stamp.AtUtc > now
            || stamp.AtUtc < command.CreatedAtUtc
            || decision != new CommandResult(id, RevisionCodec.Digest(command), OperationOutcome.Committed,
                decision.Index, slice.Term, decision.Index, ActorRevision: command.ActorRevision)
            || decision.Index <= 0 || decision.Index > slice.Head.Index || decision.Term > slice.Head.Term
            || action != VoterContinuationCommands.REQUEST && (previous is null || data.PriorDigest != previous.Decision.Digest
                || stamp.MinimumIndex < previous.Decision.Index || stamp.AtUtc < previous.Command.VoterContinuation!.Acceptance!.AtUtc)
            || (action is VoterContinuationCommands.REGISTER or VoterContinuationCommands.BIND)
                && (command.ActorUserId != slice.Authorized!.Command.ActorUserId
                    || command.ActorRevision != slice.Authorized.Command.ActorRevision))
        {
            throw new InvalidDataException("VoterContinuationSliceDecisionInvalid");
        }
    }
}
