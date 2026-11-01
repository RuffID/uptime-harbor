using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Локальный файл офлайн-инструмента; не принимается браузерным или peer API.</summary>
public record ClusterRecoveryToolRequest(ClusterOptions Target, EmergencyRecoverySelection Selection,
    OperationalNode Node, OperationalCredential Credential);
