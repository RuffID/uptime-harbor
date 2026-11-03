namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Преддоверенный обмен: неизменяемая identity отдельно от одноразовой transport-подписи.</summary>
public record EnrollmentExchange(int Version, EnrollmentRequest Request, EnrollmentProof Proof,
    string CsrDer, string? Invitation)
{
    public override string ToString()
    {
        return $"EnrollmentExchange {Request.AttemptId}";
    }
}
