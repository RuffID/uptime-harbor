namespace UptimeHarbor.Application.Users.Models;

/// <summary>Явная операция над общим аккаунтом. Автор берётся только из серверного контекста.</summary>
public record AccountChangeRequest(Guid OperationId, Guid UserId, long BaseRevision,
    string Action, string? Login = null, string? Password = null, bool? IsEnabled = null)
{
    public override string ToString()
    {
        return $"AccountChangeRequest {OperationId} for {UserId}";
    }
}
