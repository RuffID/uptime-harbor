using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

public record SummaryCut(int Version, Guid OutageId, Guid CutId, long Index, long Term,
    long MembershipVersion, long AssignmentRevision, ImmutableDictionary<string, long> SourceFrontiers,
    string ImmutableStateDigest);
