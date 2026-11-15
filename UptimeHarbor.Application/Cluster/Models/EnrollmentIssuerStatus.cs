namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Безопасный результат регистрации без ключей, сертификатов и локальных references.</summary>
public record EnrollmentIssuerStatus(Guid OperationId, Guid IssuerEpoch, string OperationStatus,
    bool IssuanceReady, long? AcceptedIndex, string? Reason);
