namespace UptimeHarbor.Application.Installation;

/// <summary>Исходная операция и проверяемый результат; Unknown не означает успех.</summary>
public record LocalAttemptStep(Guid OperationId, Guid AttemptId, string Kind, string RequestDigest,
    string ExpectedResult, long ProfileRevision, long RuntimeGeneration,
    DateTimeOffset PreparedAtUtc, string? Evidence = null);
