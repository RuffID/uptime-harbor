using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Подготовленный pre-Bind материал. Отсутствующая стадия не синтезируется при чтении.</summary>
public record LocalVoterCredentialState(LocalVoterCredentialIntent Intent,
    LocalVoterCredentialAuthorization? Authorization, EnrollmentIssuedArtifact? Artifact)
{
    public override string ToString()
    {
        return $"LocalVoterCredentialState {Intent.Request.OperationId}";
    }
}
