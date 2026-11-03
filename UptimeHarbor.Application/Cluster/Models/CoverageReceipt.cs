using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

public record CoverageReceipt(int Version, Guid ReceiptId, Guid OutageId, Guid CutId, Guid ChatId,
    long BindingCoverageRevision, long PreviousIndex, long CoveredIndex, ImmutableArray<Guid> PartIds,
    ImmutableArray<Guid> AcceptedResultIds);
