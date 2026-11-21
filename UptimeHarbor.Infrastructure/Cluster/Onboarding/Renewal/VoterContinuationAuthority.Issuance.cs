using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using Cut = UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationAudience.Cut;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public partial class VoterContinuationAuthority
{
    public const string ISSUE = "issue";

    internal async ValueTask<VoterCredentialSigner.AcceptedIssue> ConfirmIssuanceAsync(VoterCredentialProofRequest call,
        string registrationDigest, bool consumeNonce, CancellationToken token)
    {
        Cut cut = await audience.ReadAsync(token);
        AcceptedEnrollmentIssuer previousIssuer;
        if (consumeNonce)
        {
            previousIssuer = CheckProof(cut, call, registrationDigest, ISSUE);
        }
        else
        {
            previousIssuer = VoterCredentialContinuationProofs.Require(cut.Image, call, time.GetUtcNow());
        }

        CommandResult registration = RequireIssuance(cut, call, previousIssuer, registrationDigest);
        audience.RequireUnchanged(cut, cut.Image, token);
        return new(cut.Image, IssuanceDigest(call.Request, registration.Digest), registration.Index, cancellationToken =>
        {
            RequireIssuance(cut, call, previousIssuer, registrationDigest);
            audience.RequireUnchanged(cut, cut.Image, cancellationToken);
        });
    }

    internal static string IssuanceDigest(VoterCredentialRequest request, string registrationDigest)
    {
        return RevisionCodec.Digest(StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.ContinuationIssue.v1",
            Request = VoterCredentialProofs.Digest(request),
            Registration = registrationDigest
        }));
    }

    private CommandResult RequireIssuance(Cut cut, VoterCredentialProofRequest call, AcceptedEnrollmentIssuer previousIssuer,
        string registrationDigest)
    {
        if (call.Request.BoundSource is not null)
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundRecoveryCannotIssue");
        }

        AcceptedVoterContinuation accepted = ReadExact(cut, call, previousIssuer);
        if (accepted.Authorization is not { } approval || accepted.Registration is not { } registration)
        {
            throw new UnauthorizedAccessException("VoterContinuationRegistrationRequired");
        }

        CommandResult approvalDecision = Committed(cut, approval);
        CommandResult decision = Committed(cut, registration);
        VoterContinuationContext context = accepted.Request.VoterContinuation!.Context;
        VoterContinuationCommand approved = approval.VoterContinuation!;
        VoterContinuationCommand registered = registration.VoterContinuation!;
        bool currentDecision = decision.Digest == registrationDigest && decision.Term == cut.Leader.Term
            && approvalDecision.Term == decision.Term && approvalDecision.Index < decision.Index;
        bool exactApproval = registration.ActorUserId == approval.ActorUserId && registration.ActorRevision == approval.ActorRevision
            && approved.Context == context && registered.Context == context
            && approved.PriorDigest == RevisionCodec.Digest(accepted.Request) && registered.PriorDigest == approvalDecision.Digest;
        if (!currentDecision || !exactApproval)
        {
            throw new UnauthorizedAccessException("VoterContinuationRegistrationChanged");
        }

        DateTimeOffset now = time.GetUtcNow();
        RequireApplicable(cut.Image, registration, now, replay: true);
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(cut.Image, call.Request.Renewal.Issuance.IssuerEpoch, now);
        if (issuer.NodeId != cut.Leader.NodeId || issuer.StorageGeneration != cut.Profile.StorageGeneration)
        {
            throw new UnauthorizedAccessException("VoterContinuationLocalIssuerRequired");
        }

        return decision;
    }
}
