namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Неизменяемое содержимое резервирования; transport nonce и секрет находятся вне него.</summary>
public record EnrollmentRequest(int Version, Guid ClusterId, long Generation, Guid InvitationId,
    Guid AttemptId, Guid NodeId, Guid StorageGeneration, string PeerUri, string SpkiDigest, string CsrDigest)
{
    public override string ToString()
    {
        return $"EnrollmentRequest {AttemptId}";
    }
}
