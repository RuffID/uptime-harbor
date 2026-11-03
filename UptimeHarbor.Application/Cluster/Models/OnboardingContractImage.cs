using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Версионная граница snapshot для будущих import/sender сценариев, без исполнения команд.</summary>
public record OnboardingContractImage
{
    public int Version { get; init; } = 1;
    public GenesisAuthorization? Genesis { get; init; }
    public ImportBaselineRef? Import { get; init; }
    public ImmutableArray<LegacyHistory> LegacyHistory { get; init; } = [];
    public SenderAssignment? SenderAssignment { get; init; }
    public ImmutableArray<SenderOutage> SenderOutages { get; init; } = [];
    public ImmutableArray<SummaryCut> SummaryCuts { get; init; } = [];
    public ImmutableArray<CoverageReceipt> CoverageReceipts { get; init; } = [];
}
