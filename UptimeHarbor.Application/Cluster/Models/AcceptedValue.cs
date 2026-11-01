using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record AcceptedValue(Guid Id, DeletedEntityKind Kind, long Revision, ConfigurationValue Value,
    bool Deleted = false, DateTimeOffset? DeletedAtUtc = null)
{
    public override string ToString()
    {
        return $"AcceptedValue {Id} at {Revision}";
    }
}
