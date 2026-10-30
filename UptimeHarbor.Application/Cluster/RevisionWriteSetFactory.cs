using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Cluster;

public static class RevisionWriteSetFactory
{
    public static ProjectionWriteSet Create(RevisionImage before, RevisionImage after, Guid nodeId)
    {
        ClusterIdentity cluster = new(after.ClusterId, after.Generation);
        JournalPosition position = new(cluster, after.Index, after.Term);
        List<ProjectionRecord> records = [];
        List<SharedAccount> accounts = [];
        List<MonitoredServer> servers = [];
        List<MonitoredContainer> containers = [];
        List<TelegramChat> chats = [];
        List<NotificationBinding> bindings = [];
        foreach (AcceptedValue cell in after.Values.Values.OrderBy(cell => cell.Id))
        {
            if (before.Values.TryGetValue(cell.Id, out AcceptedValue? previous) && previous == cell)
            {
                continue;
            }

            if (cell.Deleted)
            {
                records.Add(new Tombstone(cell.Id, new JournalPosition(cluster, cell.Revision, after.Term),
                    cell.Kind, cell.DeletedAtUtc!.Value));
                if (cell.Kind != DeletedEntityKind.Account)
                {
                    continue;
                }
            }

            object entity = RevisionDomainMapper.Materialize(cell, position);
            switch (entity)
            {
                case SharedAccount account:
                    accounts.Add(account);
                    break;
                case MonitoredServer server:
                    servers.Add(server);
                    break;
                case MonitoredContainer container:
                    containers.Add(container);
                    break;
                case TelegramChat chat:
                    chats.Add(chat);
                    break;
                case NotificationBinding binding:
                    bindings.Add(binding);
                    break;
                case ProjectionRecord record:
                    records.Add(record);
                    break;
            }
        }

        foreach (CommandResult result in after.Results.Values.Where(result => !before.Results.ContainsKey(result.OperationId)))
        {
            records.Add(new OperationDecision(result.OperationId, result.Digest, result.Outcome,
                new JournalPosition(cluster, result.Index, result.Term), result.AppliedRevision,
                result.Reason, result.ConflictId, result.ReplacedBy));
        }

        foreach (AcceptedControl control in after.Controls.Values.Where(control => !before.Controls.ContainsKey(control.TargetId)))
        {
            records.Add(new OperationControl(control.Id, new JournalPosition(cluster, control.Index, control.Term),
                control.TargetId, control.TargetDigest, control.TargetId, control.Kind, control.ReplacementId));
        }

        foreach (ConflictVariant variant in after.Conflicts.Values)
        {
            if (before.Conflicts.TryGetValue(variant.Id, out ConflictVariant? previous) && previous == variant)
            {
                continue;
            }

            ConflictRecord conflict = new(variant.Id, new JournalPosition(cluster, variant.Index, variant.Term),
                variant.OperationId, variant.EntityId, variant.BaseRevision, variant.CurrentRevision,
                variant.BasePayload, variant.AcceptedPayload, variant.ProposedPayload, variant.CreatedAtUtc);
            if (variant.ResolutionOperationId.HasValue)
            {
                conflict.Resolve(variant.ResolutionOperationId.Value, variant.ResolvedAtUtc!.Value);
            }

            records.Add(conflict);
        }

        records.AddRange(MonitoringProjection.Changes(before, after));
        records.AddRange(ClusterOperationsProjection.Changes(before, after));

        return new ProjectionWriteSet
        {
            NodeId = nodeId, ExpectedCursorIndex = before.Index,
            Receipt = new AppliedJournalReceipt(ReceiptId(position), position, after.Digest!),
            Accounts = accounts, Records = records, Servers = servers, Containers = containers,
            Chats = chats, Bindings = bindings
        };
    }

    public static Guid ReceiptId(JournalPosition position)
    {
        string key = $"{position.Cluster.ClusterId:N}/{position.Cluster.Generation}/{position.LogIndex}/{position.Term}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
