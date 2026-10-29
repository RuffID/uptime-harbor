using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Производный immutable checkpoint принятого префикса; не замена Raft WAL.</summary>
public record RevisionImage(Guid ClusterId, long Generation)
{
    public long Index { get; init; }
    public long Term { get; init; }
    public string? Digest { get; init; }
    public string? BootstrapAccountDigest { get; init; }
    public MonitoringImage Monitoring { get; init; } = new();
    public ImmutableArray<SharedSnapshotRow> ProjectionRows { get; init; } = [];
    public ImmutableDictionary<Guid, AcceptedValue> Values { get; init; } = ImmutableDictionary<Guid, AcceptedValue>.Empty;
    public ImmutableDictionary<string, AcceptedValue> Versions { get; init; } = ImmutableDictionary<string, AcceptedValue>.Empty;
    public ImmutableDictionary<Guid, CommandResult> Results { get; init; } = ImmutableDictionary<Guid, CommandResult>.Empty;
    public ImmutableDictionary<Guid, AcceptedControl> Controls { get; init; } = ImmutableDictionary<Guid, AcceptedControl>.Empty;
    public ImmutableDictionary<Guid, ConflictVariant> Conflicts { get; init; } = ImmutableDictionary<Guid, ConflictVariant>.Empty;

    public override string ToString()
    {
        return $"RevisionImage {ClusterId}/{Generation} at {Index}";
    }
}
