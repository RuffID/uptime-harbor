using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationContext(VoterCredentialRequest Request, string RequestDigest,
    Guid AudienceNodeId, Guid AudienceStorage, long Term, long MembershipVersion, long RecoveryVersion,
    VoterContinuationProcess Process, OnlineProcessLease? Predecessor,
    Guid PreviousIssuerEpoch, long PreviousIssuerRevision, long IssuerRevision)
{
    [JsonIgnore]
    public VoterContinuationBoundSource? BoundSource => Request.BoundSource;

    public override string ToString()
    {
        return $"VoterContinuationContext {Request.OperationId}";
    }
}
