using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Исходная Bind-команда до consensus; authority и результат приёма здесь отсутствуют.</summary>
public record LocalVoterCredentialBindIntent(Guid AuthorizationOperationId, long AuthorizationIndex,
    string RequestDigest, string ArtifactDigest, RevisionCommand Command)
{
    public override string ToString()
    {
        return $"LocalVoterCredentialBindIntent {Command.OperationId}";
    }
}
