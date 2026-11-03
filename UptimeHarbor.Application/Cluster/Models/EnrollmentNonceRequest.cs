namespace UptimeHarbor.Application.Cluster.Models;

public record EnrollmentNonceRequest(int Version, Guid AttemptId, Guid BootId, string BootChallenge,
    long ProcessEpoch, string Action, string RequestDigest);
