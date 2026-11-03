namespace UptimeHarbor.Application.Installation;

/// <summary>Разрешённый browser статус без credential/reference/payload.</summary>
public record SetupStatus(string Phase, string Mode, long Revision, long Generation,
    Guid? AttemptId, string? Reason, bool CommonAccessOpen);
