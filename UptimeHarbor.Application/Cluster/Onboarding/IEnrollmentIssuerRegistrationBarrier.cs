namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Свежий majority и подтверждённая capability каждого voter перед регистрацией issuer.</summary>
public interface IEnrollmentIssuerRegistrationBarrier
{
    ValueTask ConfirmAsync(CancellationToken token);
}
