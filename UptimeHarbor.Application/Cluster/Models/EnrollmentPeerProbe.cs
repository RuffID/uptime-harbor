namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Фиксированная mTLS health-проба, без команд, invitation и полномочий восстановления.</summary>
public record EnrollmentPeerProbe(int Version, EnrollmentRequest Request, Guid RequestId, Guid FromNodeId,
    Guid ToNodeId, Guid BootId, string BootChallenge, long ProcessEpoch, string Challenge);

public record EnrollmentPeerProbeReply(int Version, EnrollmentPeerProbe Request, Guid ResponderNodeId);

public record EnrollmentPeerProbeEvidence(Guid BootId, string BootChallenge, long ProcessEpoch,
    string RequestDigest, DateTimeOffset ExpiresAtUtc);
