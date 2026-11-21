using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;
using Cut = UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationAudience.Cut;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Принимает точное продолжение credential и регистрирует процесс в consensus.</summary>
public partial class VoterContinuationAuthority(
    RevisionDispatcher dispatcher,
    RevisionStateStore revisions,
    IEnrollmentReadBarrier barrier,
    [FromKeyedServices(VoterContinuationAuthority.NONCE_KEY)] EnrollmentNonces nonces,
    IClusterStateView cluster,
    IOptions<ClusterOptions> options,
    InstallationState installation,
    IVoterContinuationAdministrator user,
    ILocalAccountLogin login,
    TimeProvider time,
    [FromKeyedServices(VoterContinuationAuthority.NONCE_KEY)] EnrollmentProbeReplay replay)
{
    public const string NONCE_KEY = "voter-continuation";
    public const string REQUEST = "request";
    public const string STATUS = "status";
    public const string REGISTER = "register";

    private readonly VoterContinuationAudience audience = new(revisions, barrier, cluster, options, installation);

    public async Task<string> IssueChallengeAsync(VoterCredentialProofRequest call, string purpose, string? decisionDigest,
        CancellationToken token, string? certificateDigest = null)
    {
        Cut cut = await audience.ReadAsync(token);
        RequireProofContext(cut, call.Request, call.Proof);
        if (purpose == BIND)
        {
            return IssueBindChallenge(cut, call, decisionDigest, certificateDigest, token);
        }

        if (certificateDigest is not null)
        {
            throw new UnauthorizedAccessException("VoterContinuationPurposeInvalid");
        }

        AcceptedEnrollmentIssuer issuer = VoterCredentialContinuationProofs.RequireChallenge(cut.Image, call, time.GetUtcNow(),
            purpose, cut.Profile.PeerAddress!, decisionDigest);
        string? binding = null;
        switch (purpose)
        {
            case REQUEST when decisionDigest is null:
                CheckRequest(cut, call, issuer);
                break;
            case STATUS when decisionDigest is null:
                binding = RevisionCodec.Digest(ReadExact(cut, call, issuer).Request);
                break;
            case REGISTER when decisionDigest is not null:
                CheckRegistration(cut, call, issuer, decisionDigest);
                binding = decisionDigest;
                break;
            case ISSUE when decisionDigest is not null:
                RequireIssuance(cut, call, issuer, decisionDigest);
                binding = decisionDigest;
                break;
            default:
                throw new UnauthorizedAccessException("VoterContinuationPurposeInvalid");
        }

        audience.RequireUnchanged(cut, cut.Image, token);
        // Первый блок подписанного клиентского nonce служит ключом process-local replay cache.
        if (!replay.Accept(new Guid(Convert.FromBase64String(call.Proof.Nonce).AsSpan(0, 16))))
        {
            throw new UnauthorizedAccessException("VoterContinuationChallengeReused");
        }

        return IssueNonce(cut, call.Request, call.Proof, purpose, binding, issuer);
    }

    public async Task<VoterContinuationStatus> ReadStatusAsync(VoterCredentialProofRequest call, CancellationToken token)
    {
        Cut cut = await audience.ReadAsync(token);
        AcceptedVoterContinuation accepted = Find(cut.Image, call.Request.OperationId)
            ?? throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        string digest = RevisionCodec.Digest(accepted.Request);
        AcceptedEnrollmentIssuer issuer = CheckProof(cut, call, digest, STATUS);
        accepted = ReadExact(cut, call, issuer);
        CommandResult request = Committed(cut, accepted.Request);
        string? authorization = null;
        CommandResult? registration = null;
        if (accepted.Authorization is { } approval)
        {
            authorization = Committed(cut, approval).Digest;
        }

        if (accepted.Registration is { } registered)
        {
            RequireApplicable(cut.Image, registered, time.GetUtcNow(), replay: true);
            registration = Committed(cut, registered);
        }

        audience.RequireUnchanged(cut, cut.Image, token);
        return new(request, authorization, registration);
    }

    internal async Task<string> IssueNonceAsync(VoterCredentialRequest request, VoterCredentialProof candidate,
        string? acceptedAuthorizationDigest, CancellationToken token)
    {
        Cut cut = await audience.ReadAsync(token);
        RequireProofContext(cut, request, candidate);
        if (acceptedAuthorizationDigest is not null)
        {
            AcceptedVoterContinuation accepted = RequireStage(cut, request.OperationId, acceptedAuthorizationDigest, authorization: true);
            VoterContinuationContext context = accepted.Request.VoterContinuation!.Context;
            if (context.Request != request || context.Process != Process(candidate))
            {
                throw new UnauthorizedAccessException("VoterContinuationIntentChanged");
            }
        }

        string purpose = REQUEST;
        if (acceptedAuthorizationDigest is not null)
        {
            purpose = REGISTER;
        }

        AcceptedEnrollmentIssuer issuer = VoterCredentialContinuationProofs.ReadPreviousIssuer(cut.Image, request.Renewal.PreviousCredentialId);
        return IssueNonce(cut, request, candidate, purpose, acceptedAuthorizationDigest, issuer);
    }

    public Task<CommandResult> RequestAsync(VoterCredentialProofRequest call, CancellationToken token)
    {
        return AcceptAsync(async cancellationToken =>
        {
            Cut cut = await audience.ReadAsync(cancellationToken);
            AcceptedEnrollmentIssuer oldIssuer = CheckProof(cut, call, null);
            return (PrepareRequest(cut, call, oldIssuer), cut);
        }, token);
    }

    public async Task<VoterContinuationAdministratorStatus> ReadAdministratorStatusAsync(Guid requestId, CancellationToken token)
    {
        Guid actor = CurrentActor();
        Cut cut = await audience.ReadAsync(token);
        RequireCurrentAdministrator(cut, actor);

        AcceptedVoterContinuation accepted = Find(cut.Image, requestId)
            ?? throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        RevisionCommand request = accepted.Request;
        RequireOtherVoter(cut, request);
        RequireContext(cut, request.VoterContinuation!.Context);
        RequireApplicable(cut.Image, request, time.GetUtcNow(), replay: true);
        CommandResult decision = Committed(cut, request);
        Guid? confirmationId = null;
        if (accepted.Authorization is { } authorization)
        {
            confirmationId = Committed(cut, authorization).OperationId;
        }

        audience.RequireUnchanged(cut, cut.Image, token);
        return new(requestId, request.EntityId, decision.Digest, confirmationId);
    }

    public Task<CommandResult> AuthorizeAsync(Guid requestId, string acceptedRequestDigest, string password, CancellationToken token)
    {
        return AcceptAsync(async cancellationToken =>
        {
            Guid actor = CurrentActor();
            Cut cut = await audience.ReadAsync(cancellationToken);
            AcceptedValue account = RequireCurrentAdministrator(cut, actor);
            AcceptedVoterContinuation accepted = RequireStage(cut, requestId, acceptedRequestDigest, authorization: false);
            RequireOtherVoter(cut, accepted.Request);
            RequireApplicable(cut.Image, accepted.Request, time.GetUtcNow(), replay: true);
            if (password.Length is < 1 or > 1024
                || !await login.CheckAsync(RevisionAccounts.Materialize(account), password, cancellationToken))
            {
                throw new UnauthorizedAccessException("VoterContinuationReauthenticationFailed");
            }

            DateTimeOffset reauthenticated = time.GetUtcNow();
            cut = await audience.ReadAsync(cancellationToken);
            accepted = RequireStage(cut, requestId, acceptedRequestDigest, authorization: false);
            if (CurrentActor() != actor || !OnboardingReducer.Active(cut.Image, actor, account.Revision)
                || accepted.Authorization is { } previous && (previous.ActorUserId != actor || previous.ActorRevision != account.Revision))
            {
                throw new UnauthorizedAccessException("VoterContinuationAdministratorChanged");
            }

            RevisionCommand command = accepted.Authorization ?? VoterContinuationCommands.Create(VoterContinuationCommands.AUTHORIZE,
                accepted.Request.VoterContinuation!.Context, reauthenticated, acceptedRequestDigest, null, actor, account.Revision);
            return (command, cut);
        }, token);
    }

    public Task<CommandResult> RegisterAsync(VoterCredentialProofRequest call, string acceptedAuthorizationDigest, CancellationToken token)
    {
        return AcceptAsync(async cancellationToken =>
        {
            Cut cut = await audience.ReadAsync(cancellationToken);
            AcceptedVoterContinuation accepted = RequireStage(cut, call.Request.OperationId, acceptedAuthorizationDigest, authorization: true);
            AcceptedEnrollmentIssuer oldIssuer = CheckProof(cut, call, acceptedAuthorizationDigest, REGISTER);
            RequireExactProof(accepted.Request, call, oldIssuer);
            RevisionCommand authorization = accepted.Authorization!;
            RequireApproval(cut, authorization);
            RevisionCommand command = accepted.Registration ?? VoterContinuationCommands.Create(VoterContinuationCommands.REGISTER,
                accepted.Request.VoterContinuation!.Context, call.Proof.IssuedAtUtc, acceptedAuthorizationDigest, Evidence(call.Proof),
                authorization.ActorUserId, authorization.ActorRevision);
            return (command, cut);
        }, token);
    }

    private static RevisionCommand PrepareRequest(Cut cut, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer oldIssuer)
    {
        VoterCredentialRequest request = call.Request;
        if (Find(cut.Image, request.OperationId) is { } accepted)
        {
            RequireExactProof(accepted.Request, call, oldIssuer);
            return accepted.Request;
        }

        EnrollmentIssuance leaf = request.Renewal.Issuance;
        AcceptedEnrollmentIssuer issuer = cut.Image.OnboardingContracts?.Issuers?.GetValueOrDefault(leaf.IssuerEpoch)
            ?? throw new UnauthorizedAccessException("VoterContinuationIssuerUnavailable");
        OnlineProcessLease? previous = cut.Image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(leaf.NodeId);
        VoterContinuationContext context = new(request, VoterCredentialProofs.Digest(request),
            cut.Leader.NodeId, cut.Profile.StorageGeneration!.Value, cut.Leader.Term, cut.Leader.MembershipVersion,
            cut.Recovery, Process(call.Proof), previous, oldIssuer.Epoch, oldIssuer.Revision, issuer.Revision);

        return VoterContinuationCommands.Create(VoterContinuationCommands.REQUEST, context,
            call.Proof.IssuedAtUtc, null, Evidence(call.Proof));
    }

    private void CheckRequest(Cut cut, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer issuer)
    {
        RevisionCommand request = PrepareRequest(cut, call, issuer);
        RequireContext(cut, request.VoterContinuation!.Context);
        RequireApplicable(cut.Image, request, time.GetUtcNow(), Find(cut.Image, call.Request.OperationId) is not null);
    }

    private AcceptedVoterContinuation ReadExact(Cut cut, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer issuer)
    {
        AcceptedVoterContinuation accepted = Find(cut.Image, call.Request.OperationId)
            ?? throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        RequireExactProof(accepted.Request, call, issuer);
        RequireContext(cut, accepted.Request.VoterContinuation!.Context);
        RequireApplicable(cut.Image, accepted.Request, time.GetUtcNow(), replay: true);
        Committed(cut, accepted.Request);
        if (accepted.Authorization is { } approval)
        {
            RequireApproval(cut, approval);
        }

        return accepted;
    }

    private void CheckRegistration(Cut cut, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer issuer, string digest)
    {
        AcceptedVoterContinuation accepted = RequireStage(cut, call.Request.OperationId, digest, authorization: true);
        RequireExactProof(accepted.Request, call, issuer);
        RequireApplicable(cut.Image, accepted.Request, time.GetUtcNow(), replay: true);
        RequireApproval(cut, accepted.Authorization!);
    }

    private void RequireApproval(Cut cut, RevisionCommand approval)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (now < approval.CreatedAtUtc || now - approval.CreatedAtUtc >= TimeSpan.FromMinutes(5))
        {
            throw new UnauthorizedAccessException("VoterContinuationAuthorizationExpiredOrChanged");
        }

        RequireApplicable(cut.Image, approval, now, replay: true);
    }

    private static CommandResult Committed(Cut cut, RevisionCommand command)
    {
        if (RevisionDispatcher.ReadVoterCredentialDecision(cut.Image, command) is not { Outcome: Domain.Cluster.OperationOutcome.Committed } decision)
        {
            throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        }

        return decision;
    }

    private Task<CommandResult> AcceptAsync(Func<CancellationToken, Task<(RevisionCommand Command, Cut Cut)>> prepare,
        CancellationToken token)
    {
        Cut? confirmed = null;
        return dispatcher.AcceptVoterContinuationAsync(async cancellationToken =>
        {
            var (command, cut) = await prepare(cancellationToken);
            VoterContinuationCommand payload = command.VoterContinuation!;
            RequireContext(cut, payload.Context);
            DateTimeOffset now = time.GetUtcNow();
            bool replay = RevisionDispatcher.ReadVoterCredentialDecision(cut.Image, command) is not null;
            RequireApplicable(cut.Image, command, now, replay);
            audience.RequireUnchanged(cut, cut.Image, cancellationToken);
            confirmed = cut;
            RevisionCommand stamped = command with
            {
                VoterContinuation = payload with
                {
                    Acceptance = new(now, cut.Image.Index, cut.Leader.Term, cut.Leader.MembershipVersion, cut.Recovery)
                }
            };
            return (stamped, cut.Image, cut.Recovery, cut.Leader);
        }, (image, command) =>
        {
            audience.RequireUnchanged(confirmed!, image, token);
            RequireApplicable(image, command, time.GetUtcNow(), replay: true);
            if (command.Action == VoterContinuationCommands.AUTHORIZE && CurrentActor() != command.ActorUserId)
            {
                throw new UnauthorizedAccessException("VoterContinuationAdministratorChanged");
            }
        }, token);
    }

    private AcceptedEnrollmentIssuer CheckProof(Cut cut, VoterCredentialProofRequest call, string? binding,
        string purpose = REQUEST, RevisionImage? proofImage = null)
    {
        VoterCredentialProof proof = call.Proof;
        RequireProofContext(cut, call.Request, proof);
        AcceptedEnrollmentIssuer issuer = VoterCredentialContinuationProofs.Require(proofImage ?? cut.Image, call, time.GetUtcNow());
        EnrollmentProof nonce = new(1, call.Request.OperationId, VoterCredentialContinuationProofs.SCOPE, proof.BootId,
            proof.BootChallenge, proof.ProcessEpoch, proof.Nonce, NonceDigest(cut, call.Request, proof, purpose, binding, issuer), string.Empty);
        if (!nonces.Consume(call.Request.OperationId, nonce))
        {
            throw new UnauthorizedAccessException("VoterContinuationNonceUnavailable");
        }

        return issuer;
    }

    private static void RequireProofContext(Cut cut, VoterCredentialRequest request, VoterCredentialProof proof)
    {
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        if (request.ClusterId != cut.Image.ClusterId || request.Generation != cut.Image.Generation
            || !cut.Leader.VotingNodes.Contains(leaf.NodeId) || proof.Scope != VoterCredentialContinuationProofs.SCOPE
            || proof.OriginNodeId != leaf.NodeId || proof.OriginStorageGeneration != leaf.StorageGeneration
            || proof.MembershipVersion != cut.Leader.MembershipVersion || proof.RecoveryVersion < 0)
        {
            throw new UnauthorizedAccessException("VoterContinuationProofContextChanged");
        }
    }

    private static void RequireContext(Cut cut, VoterContinuationContext context)
    {
        if (context.AudienceNodeId != cut.Leader.NodeId || context.AudienceStorage != cut.Profile.StorageGeneration
            || context.Term != cut.Leader.Term || context.RecoveryVersion != cut.Recovery
            || context.MembershipVersion != cut.Leader.MembershipVersion)
        {
            throw new UnauthorizedAccessException("VoterContinuationContextChanged");
        }
    }

    private static void RequireExactProof(RevisionCommand request, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer issuer)
    {
        VoterContinuationContext context = request.VoterContinuation!.Context;
        if (context.Request != call.Request || context.Process != Process(call.Proof)
            || context.PreviousIssuerEpoch != issuer.Epoch || context.PreviousIssuerRevision != issuer.Revision)
        {
            throw new UnauthorizedAccessException("VoterContinuationIntentChanged");
        }
    }

    private static AcceptedVoterContinuation RequireStage(Cut cut, Guid id, string digest, bool authorization)
    {
        AcceptedVoterContinuation? accepted = Find(cut.Image, id);
        RevisionCommand? command = accepted?.Request;
        if (authorization)
        {
            command = accepted?.Authorization;
        }

        if (command is null || digest != RevisionCodec.Digest(command)
            || RevisionDispatcher.ReadVoterCredentialDecision(cut.Image, command)?.Outcome != Domain.Cluster.OperationOutcome.Committed)
        {
            throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        }

        RequireContext(cut, command.VoterContinuation!.Context);
        return accepted!;
    }

    private Guid CurrentActor()
    {
        if (user.CommonActorId is not Guid actor || actor == Guid.Empty)
        {
            throw new UnauthorizedAccessException("VoterContinuationAdministratorRequired");
        }

        return actor;
    }

    private AcceptedValue RequireCurrentAdministrator(Cut cut, Guid actor)
    {
        AcceptedValue? account = cut.Image.Values.GetValueOrDefault(actor);
        if (CurrentActor() != actor || account is null || !OnboardingReducer.Active(cut.Image, actor, account.Revision))
        {
            throw new UnauthorizedAccessException("VoterContinuationAdministratorRequired");
        }

        return account;
    }

    private static void RequireOtherVoter(Cut cut, RevisionCommand request)
    {
        if (request.EntityId == cut.Leader.NodeId)
        {
            throw new UnauthorizedAccessException("VoterContinuationOtherVoterRequired");
        }
    }

    private static void RequireApplicable(RevisionImage image, RevisionCommand command, DateTimeOffset now, bool replay)
    {
        string? refusal = VoterContinuationReducer.Refusal(image, command, now, image.Index, replay);
        if (refusal is not null)
        {
            throw new UnauthorizedAccessException(refusal);
        }
    }

    private static AcceptedVoterContinuation? Find(RevisionImage image, Guid id)
    {
        return image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(id);
    }

    private static VoterContinuationProcess Process(VoterCredentialProof proof)
    {
        return new(proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.RecoveryVersion);
    }

    private static VoterContinuationEvidence Evidence(VoterCredentialProof proof)
    {
        return new(proof.Nonce, RevisionCodec.Digest(StrictOnboardingJson.Encode(proof)));
    }

    private string IssueNonce(Cut cut, VoterCredentialRequest request, VoterCredentialProof proof, string purpose, string? binding,
        AcceptedEnrollmentIssuer previousIssuer)
    {
        return nonces.Issue(request.OperationId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch,
            VoterCredentialContinuationProofs.SCOPE, NonceDigest(cut, request, proof, purpose, binding, previousIssuer));
    }

    private static string NonceDigest(Cut cut, VoterCredentialRequest request, VoterCredentialProof proof, string purpose, string? binding,
        AcceptedEnrollmentIssuer previousIssuer)
    {
        return RevisionCodec.Digest(StrictOnboardingJson.Encode(new
        {
            Intent = VoterCredentialProofs.Digest(request), Process = Process(proof),
            cut.Recovery, cut.Leader.Term, cut.Leader.MembershipVersion,
            Audience = cut.Leader.NodeId, cut.Profile.StorageGeneration, Purpose = purpose, Binding = binding,
            PreviousIssuerEpoch = previousIssuer.Epoch, PreviousIssuerRevision = previousIssuer.Revision,
            IssuerRevision = cut.Image.OnboardingContracts?.Issuers?.GetValueOrDefault(request.Renewal.Issuance.IssuerEpoch)?.Revision
        }));
    }

}
