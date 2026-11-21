using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Cut = UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationAudience.Cut;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public partial class VoterContinuationAuthority
{
    public const string BIND = "bind";

    internal static string BindChallengeDigest(string? registrationDigest, string? certificateDigest)
    {
        if (registrationDigest is null || certificateDigest is null
            || StorageGuards.Digest(registrationDigest) != registrationDigest
            || StorageGuards.Digest(certificateDigest) != certificateDigest)
        {
            throw new InvalidDataException("VoterContinuationBindDigestInvalid");
        }

        return RevisionCodec.Digest(StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.ContinuationBind.v1",
            Registration = registrationDigest, Certificate = certificateDigest
        }));
    }

    internal Task<CommandResult> BindAsync(VoterCredentialProofRequest call, string registrationDigest, string certificateDigest,
        IEnrollmentSigningUnitOfWork work, CancellationToken token)
    {
        string binding = BindChallengeDigest(registrationDigest, certificateDigest);
        Cut? confirmed = null;
        return dispatcher.AcceptVoterContinuationAsync(async cancellationToken =>
        {
            Cut cut = await audience.ReadAsync(cancellationToken);
            var stage = BindStage(cut, call.Request, registrationDigest, certificateDigest);
            AcceptedEnrollmentIssuer oldIssuer = CheckProof(cut, call, binding, BIND, stage.ProofImage);
            RequireBindStage(cut, stage.ProofImage, call, oldIssuer, registrationDigest, cancellationToken);

            EnrollmentIssuance leaf = call.Request.Renewal.Issuance;
            EnrollmentIssuedArtifact artifact = await work.Signing.FindIssueAsync(leaf.IssueId, cancellationToken)
                ?? throw new UnauthorizedAccessException("VoterContinuationIssuedLeafRequired");
            if (artifact.CertificateDigest != certificateDigest)
            {
                throw new UnauthorizedAccessException("VoterContinuationIssuedLeafChanged");
            }

            cut = await audience.ReadAsync(cancellationToken);
            stage = BindStage(cut, call.Request, registrationDigest, certificateDigest);
            DateTimeOffset now = time.GetUtcNow();
            oldIssuer = VoterCredentialContinuationProofs.Require(stage.ProofImage, call, now);
            CommandResult registered = RequireBindStage(cut, stage.ProofImage, call, oldIssuer,
                registrationDigest, cancellationToken);
            VoterCredentialArtifactValidation.RequireIssued(cut.Image, call.Request, artifact,
                IssuanceDigest(call.Request, registered.Digest), registered.Index, now);

            OperationalCredential credential = new(leaf.IssueId, leaf.NodeId, artifact.CertificateDigest,
                Convert.ToBase64String(artifact.CertificateDer), leaf.NotAfterUtc, null, null);
            RevisionCommand registration = stage.Accepted.Registration!;
            RevisionCommand command = stage.Accepted.Bind ?? VoterContinuationCommands.Create(VoterContinuationCommands.BIND,
                registration.VoterContinuation!.Context, call.Proof.IssuedAtUtc, registered.Digest, Evidence(call.Proof),
                registration.ActorUserId, registration.ActorRevision, credential);
            if (command.VoterContinuation!.Credential != credential)
            {
                throw new UnauthorizedAccessException("VoterContinuationIssuedLeafChanged");
            }

            bool replay = stage.Accepted.Bind is not null;
            DateTimeOffset writeTime = time.GetUtcNow();
            RequireBinding(cut.Image, command, writeTime, replay);
            audience.RequireUnchanged(cut, cut.Image, cancellationToken);
            if (!replay)
            {
                command = command with
                {
                    VoterContinuation = command.VoterContinuation with
                    {
                        Acceptance = new(writeTime, cut.Image.Index, cut.Leader.Term, cut.Leader.MembershipVersion, cut.Recovery)
                    }
                };
            }

            confirmed = cut;
            return (command, cut.Image, cut.Recovery, cut.Leader);
        }, (image, command) =>
        {
            audience.RequireUnchanged(confirmed!, image, token);
            if (CommittedBind(image, command))
            {
                RequireBinding(image, command, time.GetUtcNow(), replay: true);
            }
        }, token);
    }

    private string IssueBindChallenge(Cut cut, VoterCredentialProofRequest call, string? registrationDigest,
        string? certificateDigest, CancellationToken token)
    {
        string binding = BindChallengeDigest(registrationDigest, certificateDigest);
        var stage = BindStage(cut, call.Request, registrationDigest!, certificateDigest!);
        AcceptedEnrollmentIssuer issuer = VoterCredentialContinuationProofs.RequireChallenge(stage.ProofImage, call,
            time.GetUtcNow(), BIND, cut.Profile.PeerAddress!, binding);
        RequireBindStage(cut, stage.ProofImage, call, issuer, registrationDigest!, token);
        if (!replay.Accept(new Guid(Convert.FromBase64String(call.Proof.Nonce).AsSpan(0, 16))))
        {
            throw new UnauthorizedAccessException("VoterContinuationChallengeReused");
        }

        return IssueNonce(cut, call.Request, call.Proof, BIND, binding, issuer);
    }

    private static (AcceptedVoterContinuation Accepted, RevisionImage ProofImage) BindStage(Cut cut, VoterCredentialRequest request,
        string registrationDigest, string certificateDigest)
    {
        AcceptedVoterContinuation accepted = Find(cut.Image, request.OperationId)
            ?? throw new UnauthorizedAccessException("VoterContinuationAcceptedStageRequired");
        RequireContext(cut, accepted.Request.VoterContinuation!.Context);
        if (accepted.Request.VoterContinuation.Context.Request != request
            || accepted.Registration is not { } registration || Committed(cut, registration).Digest != registrationDigest)
        {
            throw new UnauthorizedAccessException("VoterContinuationRegistrationChanged");
        }

        if (accepted.Bind is not { } bind)
        {
            return (accepted, cut.Image);
        }

        if (bind.VoterContinuation!.Credential?.Fingerprint != certificateDigest || !VoterContinuationBinding.IsCurrentBinding(cut.Image, bind))
        {
            throw new UnauthorizedAccessException("VoterContinuationBindingChanged");
        }

        return (accepted, VoterContinuationBinding.BeforeBinding(cut.Image, request.Renewal.Issuance.IssueId));
    }

    private CommandResult RequireBindStage(Cut cut, RevisionImage proofImage,
        VoterCredentialProofRequest call, AcceptedEnrollmentIssuer oldIssuer, string registrationDigest, CancellationToken token)
    {
        RequireProofContext(cut, call.Request, call.Proof);
        CommandResult registered = RequireIssuance(cut with { Image = proofImage }, call, oldIssuer, registrationDigest);
        audience.RequireUnchanged(cut, cut.Image, token);
        return registered;
    }

    private static bool CommittedBind(RevisionImage image, RevisionCommand command)
    {
        return RevisionDispatcher.ReadVoterCredentialDecision(image, command)?.Outcome == OperationOutcome.Committed;
    }

    private static void RequireBinding(RevisionImage image, RevisionCommand command, DateTimeOffset now, bool replay)
    {
        string? refusal = VoterContinuationBinding.Refusal(image, command, now, image.Index, replay);
        if (refusal is not null)
        {
            throw new UnauthorizedAccessException(refusal);
        }
    }
}
