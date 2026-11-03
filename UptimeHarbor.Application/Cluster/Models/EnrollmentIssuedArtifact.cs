namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Локальный durable результат signer, а не operational credential.</summary>
public record EnrollmentIssuedArtifact(Guid IssueId, Guid IssuerEpoch, Guid AttemptId, string RequestDigest,
    string TemplateDigest, Guid? GenesisId, long AuthorizationIndex, byte[] CertificateDer, string CertificateDigest)
{
    public override string ToString()
    {
        return $"EnrollmentIssuedArtifact {IssueId}";
    }
}
