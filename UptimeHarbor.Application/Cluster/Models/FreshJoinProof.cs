namespace UptimeHarbor.Application.Cluster.Models;

public sealed record FreshJoinProof(Guid AttemptId, Guid GrantId, Guid BootId, string BootChallenge,
    long ProcessEpoch, long AdmissionIndex) : RecoveryProof;
