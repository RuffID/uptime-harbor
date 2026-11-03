using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Точный process-local permit для двух записей genesis. Не является общим CanWork.</summary>
public class GenesisWriterPermit
{
    private readonly object sync = new();
    private GenesisAuthorization? genesis;
    private Guid initializeId;
    private string? initializeDigest;
    private Guid accountId;
    private string? accountDigest;

    internal void Arm(GenesisAuthorization authorization, RevisionCommand initialize, RevisionCommand account)
    {
        OnboardingDataCodec.Validate(authorization);
        RevisionCodec.Validate(initialize);
        RevisionCodec.Validate(account);
        string initializeHash = RevisionCodec.Digest(initialize);
        string accountHash = RevisionCodec.Digest(account);
        bool exact = !authorization.MigrationId.HasValue && initialize.Action == "InitializeCluster"
            && initialize.EnrollmentTrust?.Genesis == authorization && initialize.OperationId == authorization.GenesisId
            && initialize.OriginNodeId == authorization.NodeId && account.Action == "BootstrapAccount"
            && account.ClusterId == authorization.ClusterId && account.ClusterGeneration == authorization.Generation
            && account.OriginNodeId == authorization.NodeId && accountHash == authorization.AccountOrImportDigest;
        if (!exact)
        {
            throw new InvalidDataException("GenesisPermitBindingInvalid");
        }

        lock (sync)
        {
            if (genesis is not null && (genesis != authorization || initializeId != initialize.OperationId
                || initializeDigest != initializeHash || accountId != account.OperationId || accountDigest != accountHash))
            {
                throw new InvalidOperationException("GenesisPermitAlreadyBound");
            }

            genesis = authorization;
            initializeId = initialize.OperationId;
            initializeDigest = initializeHash;
            accountId = account.OperationId;
            accountDigest = accountHash;
        }
    }

    public bool Allows(RevisionCommand command, RevisionImage image)
    {
        lock (sync)
        {
            if (genesis is null || image.OnboardingContracts?.Genesis != genesis
                || image.ClusterId != genesis.ClusterId || image.Generation != genesis.Generation
                || command.OriginNodeId != genesis.NodeId || image.BootstrapAccountDigest != accountDigest
                || image.RecoveryBaseline is not null || image.Operations.MaintenanceId != Guid.Empty)
            {
                return false;
            }

            // Существующий result возвращает dispatcher, повтор не пишет новый native effect.
            if (image.Results.ContainsKey(accountId) || image.Values.Values.Any(value => value.Kind == DeletedEntityKind.Account))
            {
                return false;
            }

            if (command.Action == "InitializeCluster")
            {
                return command.OperationId == initializeId && RevisionCodec.Digest(command) == initializeDigest
                    && !image.OnboardingContracts.GenesisInitialized && image.Operations.Nodes.IsEmpty
                    && image.Operations.Credentials.IsEmpty && image.Monitoring.Nodes.IsEmpty && image.Values.IsEmpty;
            }

            return command.Action == "BootstrapAccount" && command.OperationId == accountId
                && RevisionCodec.Digest(command) == accountDigest && image.OnboardingContracts.GenesisInitialized
                && image.Results.GetValueOrDefault(initializeId)?.Outcome == OperationOutcome.Committed
                && image.Operations.Nodes.Count == 1 && image.Operations.Nodes.ContainsKey(genesis.NodeId);
        }
    }
}
