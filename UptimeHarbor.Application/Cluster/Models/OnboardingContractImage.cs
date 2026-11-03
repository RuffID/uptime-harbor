using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Версионная граница snapshot для будущих import/sender сценариев, без исполнения команд.</summary>
public record OnboardingContractImage
{
    public int Version { get; init; } = 1;
    public GenesisAuthorization? Genesis { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool GenesisInitialized { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>? Issuers { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<Guid, AcceptedOnlineGrant>? OnlineGrants { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<Guid, OnlineProcessLease>? OnlineProcesses { get; init; }
    public ImportBaselineRef? Import { get; init; }
    public ImmutableArray<LegacyHistory> LegacyHistory { get; init; } = [];
    public SenderAssignment? SenderAssignment { get; init; }
    public ImmutableArray<SenderOutage> SenderOutages { get; init; } = [];
    public ImmutableArray<SummaryCut> SummaryCuts { get; init; } = [];
    public ImmutableArray<CoverageReceipt> CoverageReceipts { get; init; } = [];
}
