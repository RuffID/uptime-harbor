using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Свежий majority cut точной принятой выдачи; не локальный факт или подпись CA.</summary>
public interface IEnrollmentSigningAuthorization
{
    ValueTask<long> ConfirmAsync(EnrollmentRequest request, EnrollmentIssuance issuance,
        EnrollmentIssuerReference localIssuer, CancellationToken token);
}
