namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Исходная команда gateway с PoP. Reserve содержит verifier вместо plaintext приглашения.</summary>
public record EnrollmentCoordinatedRequest(int Version, RevisionCommand Command, EnrollmentRequest Request,
    EnrollmentProof Proof, string CsrDer, EnrollmentIssuedArtifact? Artifact);
