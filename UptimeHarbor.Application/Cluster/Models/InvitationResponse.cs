namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Browser whitelist. Код существует только в первом ответе подтверждённой выдачи.</summary>
public record InvitationResponse(Guid OperationId, Guid InvitationId, string OperationStatus,
    string? InvitationStatus, DateTimeOffset? ExpiresAtUtc, Guid? AttemptId, string? AttemptStatus, string? Reason)
{
    public string? Code { get; init; }

    public override string ToString()
    {
        return $"InvitationResponse {OperationId} {OperationStatus}";
    }
}
