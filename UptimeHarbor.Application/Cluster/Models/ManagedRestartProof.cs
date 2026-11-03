namespace UptimeHarbor.Application.Cluster.Models;

public sealed record ManagedRestartProof(Guid InstallIdentity, Guid BootId, string BootChallenge,
    string SealedStorageDigest) : RecoveryProof
{
    public Guid ActorId { get; init; }
}
