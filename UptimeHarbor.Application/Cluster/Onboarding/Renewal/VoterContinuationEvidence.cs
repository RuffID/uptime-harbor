namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationEvidence(string Nonce, string ProofDigest)
{
    public override string ToString()
    {
        return nameof(VoterContinuationEvidence);
    }
}
