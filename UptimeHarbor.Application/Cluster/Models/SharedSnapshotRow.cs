using System.Collections.Immutable;
using System.Text.Json;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Внутренние общие scalar-поля схемы snapshot; локальные Identity-поля сюда не входят.</summary>
public record SharedSnapshotRow(string Entity, ImmutableDictionary<string, JsonElement> Fields)
{
    public override string ToString()
    {
        return "[protected snapshot row]";
    }
}
