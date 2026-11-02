using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Abstractions.Persistence;

// Готовые изменения доказанного принятого префикса; подача команд выполняется отдельно.
public class ProjectionWriteSet
{
    public required Guid NodeId { get; init; }
    public required long ExpectedCursorIndex { get; init; }
    public required AppliedJournalReceipt Receipt { get; init; }
    public SharedAccount? Account { get; init; }
    public IReadOnlyList<SharedAccount> Accounts { get; init; } = [];
    public IReadOnlyList<ProjectionRecord> Records { get; init; } = [];
    public IReadOnlyList<MonitoredServer> Servers { get; init; } = [];
    public IReadOnlyList<TelegramChat> Chats { get; init; } = [];
    public IReadOnlyList<NotificationBinding> Bindings { get; init; } = [];
}
