using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Направляет запрос владельцу принятого issuer epoch.</summary>
public class VoterCredentialIssuanceRouter(VoterCredentialSigner signer, IVoterCredentialSignerClient peers,
    VoterCredentialRenewalAuthority authority, IOptions<ClusterOptions> options, TimeProvider time)
{
    public async Task<EnrollmentIssuedArtifact> IssueAsync(VoterCredentialSigningRequest call, CancellationToken token)
    {
        RevisionImage image = await authority.ConfirmSigningAsync(call, options.Value.NodeId, options.Value.StorageGeneration, token);
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image,
            call.ProofRequest.Request.Renewal.Issuance.IssuerEpoch, time.GetUtcNow());
        EnrollmentIssuedArtifact artifact;
        if (issuer.NodeId == options.Value.NodeId)
        {
            artifact = await signer.IssueAsync(call, options.Value.NodeId, options.Value.StorageGeneration, token);
        }
        else
        {
            artifact = await peers.IssueAsync(issuer.NodeId, call, token);
        }

        image = await authority.ConfirmSigningAsync(call, options.Value.NodeId, options.Value.StorageGeneration, token);
        VoterCredentialArtifactValidation.Require(image, call, artifact, time.GetUtcNow(), authority.LegacyTrust);
        return artifact;
    }
}
