using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IProjectionRepository
{
    Task<ProjectionCursor?> GetCursorAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<AppliedJournalReceipt?> GetReceiptAsync(JournalPosition position, CancellationToken cancellationToken = default);
    Task<OperationDecision?> GetDecisionAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task StageAsync(ProjectionWriteSet changes, CancellationToken cancellationToken = default);
    void AddCursor(ProjectionCursor cursor);
    void AddReceipt(AppliedJournalReceipt receipt);
}
