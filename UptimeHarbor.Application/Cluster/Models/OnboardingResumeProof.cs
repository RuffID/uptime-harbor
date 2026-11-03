namespace UptimeHarbor.Application.Cluster.Models;

public sealed record OnboardingResumeProof(Guid AttemptId, Guid GrantId, Guid BootId, string BootChallenge,
    long ProcessEpoch, long ResumeIndex) : RecoveryProof;
