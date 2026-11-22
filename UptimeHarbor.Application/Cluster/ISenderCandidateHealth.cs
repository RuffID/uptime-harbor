using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Свежая проверка бота на кандидате по локальному или аутентифицированному peer health.</summary>
public interface ISenderCandidateHealth
{
    bool IsReady(Guid nodeId, ConsensusStatus leader);
}
