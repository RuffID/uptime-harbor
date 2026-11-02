using System.Collections.Immutable;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Преобразует прежнюю проекцию мониторинга после проверки целостности снимка.</summary>
public static class SnapshotMonitoringCompatibility
{
    private const string LEGACY_APPLICATION = "UptimeHarbor.Domain.Monitoring.MonitoredContainer";
    private static readonly HashSet<string> COMMON_FIELDS =
    [
        "Id", "CheckGeneration", "ConfigRevision", "ConsecutiveFailures", "CurrentIncidentId",
        "Description", "EndpointUrl", "IsMonitoringEnabled", "LastCheckedAtUtc", "LastKnownName",
        "LastSuccessfulCheckAtUtc", "StartedAtUtc", "Status", "Version"
    ];

    public static RevisionImage Upgrade(RevisionImage image)
    {
        ImmutableArray<SharedSnapshotRow>.Builder rows = ImmutableArray.CreateBuilder<SharedSnapshotRow>();
        foreach (SharedSnapshotRow row in image.ProjectionRows)
        {
            bool application = row.Entity == LEGACY_APPLICATION;
            bool server = row.Entity == typeof(MonitoredServer).FullName && !row.Fields.ContainsKey("Type");
            if (!application && !server)
            {
                rows.Add(row);
                continue;
            }

            HashSet<string> expected = new(COMMON_FIELDS, StringComparer.Ordinal);
            if (application)
            {
                expected.Add("ServerId");
            }
            else
            {
                expected.Add("Name");
                expected.Add("NormalizedName");
            }

            if (!expected.SetEquals(row.Fields.Keys))
            {
                throw new InvalidDataException("LegacyMonitoringSnapshotSchemaMismatch");
            }

            ImmutableDictionary<string, JsonElement> fields = row.Fields;
            if (application)
            {
                Guid id = fields["Id"].GetGuid();
                string name = $"Приложение {id:N}";
                if (image.Values.TryGetValue(id, out AcceptedValue? accepted)
                    && accepted.Kind == DeletedEntityKind.Container && !accepted.Deleted
                    && accepted.Value.Name is not null)
                {
                    name = accepted.Value.Name.Trim();
                }
                fields = fields.Add("Type", JsonSerializer.SerializeToElement(ServerType.Application))
                    .Add("Name", JsonSerializer.SerializeToElement(name))
                    .Add("NormalizedName", JsonSerializer.SerializeToElement(name.ToUpperInvariant()));
            }
            else
            {
                fields = fields.Add("Type", JsonSerializer.SerializeToElement(ServerType.Server))
                    .Add("ServerId", JsonSerializer.SerializeToElement<Guid?>(null));
            }

            rows.Add(new SharedSnapshotRow(typeof(MonitoredServer).FullName!, fields));
        }

        return image with { ProjectionRows = rows.ToImmutable() };
    }
}
