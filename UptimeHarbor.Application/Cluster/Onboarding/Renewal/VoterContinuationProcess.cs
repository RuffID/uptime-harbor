namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationProcess(Guid BootId, string BootChallenge, long Epoch, long RecoveryVersion)
{
    public override string ToString()
    {
        return $"VoterContinuationProcess {BootId}";
    }
}
