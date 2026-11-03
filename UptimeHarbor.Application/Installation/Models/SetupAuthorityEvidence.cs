using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Installation;

/// <summary>Ссылка на проверяемый authority результат, а не флаг Ready от клиента.</summary>
public record SetupAuthorityEvidence(Guid AttemptId, long RuntimeGeneration, long AcceptedIndex,
    string Digest, SetupAuthorityEvidenceKind Kind)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PreparedGenesis? Genesis { get; init; }
    public EnrollmentDescriptor? Enrollment { get; init; }
    public string? TrustReference { get; init; }
}
