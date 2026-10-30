using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Принятые эксплуатационные решения; протокольный состав по-прежнему принадлежит DotNext.</summary>
public record ClusterOperationsImage
{
    public ImmutableDictionary<Guid, OperationalNode> Nodes { get; init; } = ImmutableDictionary<Guid, OperationalNode>.Empty;
    public ImmutableDictionary<Guid, OperationalCredential> Credentials { get; init; } = ImmutableDictionary<Guid, OperationalCredential>.Empty;
    public ImmutableDictionary<Guid, MembershipOperation> Changes { get; init; } = ImmutableDictionary<Guid, MembershipOperation>.Empty;
    public Guid MaintenanceId { get; init; }
    public long Revision { get; init; }
    public SenderAuthorityKey? Sender { get; init; }
    public long BotId { get; init; }
    public long MembershipVersion { get; init; }
}
