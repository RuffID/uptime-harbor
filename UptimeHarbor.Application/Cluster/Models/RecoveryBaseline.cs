using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Происхождение выбранного принятого среза; исходные authority попыток не переписываются.</summary>
public record RecoveryBaseline(Guid RecoveryId, Guid PreviousClusterId, long PreviousGeneration,
    long SourceIndex, long SourceTerm, string SourceDigest, long? KnownCommittedIndex,
    Guid AdministratorId, Guid OperatorId, string IsolationDigest, DateTimeOffset ConfirmedAtUtc,
    ClusterOperationsImage PreviousOperations)
{
    public ImmutableArray<RecoveryBaseline> Ancestors { get; init; } = [];
    public ImmutableDictionary<Guid, RecoveredAttemptIdentity> Attempts { get; init; } = ImmutableDictionary<Guid, RecoveredAttemptIdentity>.Empty;
}
