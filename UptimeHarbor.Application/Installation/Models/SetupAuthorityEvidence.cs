namespace UptimeHarbor.Application.Installation;

/// <summary>Ссылка на проверяемый authority результат, а не флаг Ready от клиента.</summary>
public record SetupAuthorityEvidence(Guid AttemptId, long RuntimeGeneration, long AcceptedIndex,
    string Digest, SetupAuthorityEvidenceKind Kind);
