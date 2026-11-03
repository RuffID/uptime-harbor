namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Подтверждает текущий registry перед ответом о наличии/отсутствии reservation.</summary>
public interface IEnrollmentReadBarrier
{
    ValueTask ConfirmAsync(CancellationToken token);
}
