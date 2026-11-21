using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Выдача по exact accepted регистрации через отдельный HTTPS proof.</summary>
public class VoterContinuationIssuance(VoterContinuationAuthority authority, VoterCredentialSigner signer)
{
    public Task<EnrollmentIssuedArtifact> IssueAsync(VoterCredentialProofRequest call, string registrationDigest, CancellationToken token)
    {
        return signer.IssueContinuationAsync(call, registrationDigest, authority, token);
    }
}
