using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Выбирает только accepted owner epoch; недоступный signer не заменяется локальным issuer.</summary>
public class EnrollmentIssuanceRouter(EnrollmentSigner certificates, OnlineGrantSigner grants,
    RevisionStateStore revisions, IEnrollmentPeerClient peers, IOptions<ClusterOptions> options,
    TimeProvider time, OnlineGrantRequests? grantRequests = null)
{
    public async Task<EnrollmentIssuedArtifact> IssueAsync(EnrollmentRequest request, EnrollmentIssuance template,
        byte[] csr, CancellationToken token)
    {
        RevisionImage image = RequireImage();
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, template.IssuerEpoch, time.GetUtcNow());
        EnrollmentIssuedArtifact artifact;
        if (issuer.NodeId == options.Value.NodeId)
        {
            artifact = await certificates.IssueAsync(request, template, csr, token);
        }
        else
        {
            OnboardingAttempt attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt
                ?? throw new UnauthorizedAccessException("EnrollmentIssuanceRequired");
            EnrollmentSigningRequest call = new(1, request, template, attempt.IssuanceAuthorizationIndex, Convert.ToBase64String(csr));
            artifact = await peers.IssueAsync(issuer.NodeId, call, token);
        }

        EnrollmentArtifactValidation.Require(RequireImage(), request, template, artifact, time.GetUtcNow());
        return artifact;
    }

    public async Task<OnlineGrant> SignAsync(OnlineGrant grant, CancellationToken token)
    {
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(RequireImage(), grant.IssuerEpoch, time.GetUtcNow());
        OnlineGrant expected = grant;
        OnlineGrant signed;
        if (issuer.NodeId == options.Value.NodeId)
        {
            signed = await grants.SignAsync(grant, token);
        }
        else
        {
            if (grantRequests is null)
            {
                throw new UnauthorizedAccessException("OnlineGrantLocalSourceRequired");
            }

            OnlineGrantSigningRequest request = await grantRequests.PrepareAsync(grant, token);
            expected = request.Grant;
            signed = await peers.SignGrantAsync(issuer.NodeId, request, token);
        }

        RevisionImage image = RequireImage();
        if (signed with { Signature = string.Empty } != expected
            || !OnlineGrantSignatures.Verify(image, signed, time.GetUtcNow())
            || !OnlineGrantPolicy.Current(image, signed, time.GetUtcNow(), signed.Purpose == OnlineGrantPurpose.Process))
        {
            throw new UnauthorizedAccessException("OnlineGrantSignerResponseInvalid");
        }

        return signed;
    }

    private RevisionImage RequireImage()
    {
        if (!revisions.Available || revisions.Current is null || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY)
        {
            throw new InvalidOperationException("EnrollmentSigningProjectionUnavailable");
        }

        return revisions.Current;
    }
}
