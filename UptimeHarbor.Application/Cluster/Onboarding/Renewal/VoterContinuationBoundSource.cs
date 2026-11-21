namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationBoundSource(Guid RequestId, string BindDigest, long CredentialRevision);
