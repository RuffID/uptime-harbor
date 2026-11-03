namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Transport PoP отделён от immutable request и его OperationId.</summary>
public record EnrollmentProof(int Version, Guid RequestId, string Action, Guid BootId, string BootChallenge,
    long ProcessEpoch, string Nonce, string RequestDigest, string Signature)
{
    public override string ToString()
    {
        return $"EnrollmentProof {RequestId}";
    }
}
