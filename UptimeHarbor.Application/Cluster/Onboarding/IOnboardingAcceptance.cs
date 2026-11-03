using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Сверка majority/clock/schema и криптографических доказательств до записи команд нового протокола.</summary>
public interface IOnboardingAcceptance
{
    ValueTask<RevisionCommand> PrepareAsync(RevisionCommand command, CancellationToken token);
}
