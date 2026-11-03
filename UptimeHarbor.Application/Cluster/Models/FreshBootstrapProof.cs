namespace UptimeHarbor.Application.Cluster.Models;

public sealed record FreshBootstrapProof(GenesisAuthorization Authorization, RevisionCommand InitializeCluster,
    Guid BootId, string BootChallenge) : RecoveryProof;
