using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Будущая проверка CSR/PoP/nonce, issuer и продолжений; локальная запись не даёт допуска.</summary>
public interface IEnrollmentAuthority
{
    ValueTask<bool> VerifyAsync(RevisionCommand command, CancellationToken token);
}
