using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Внутренний snapshot последнего аутентифицированного health, не браузерный DTO.</summary>
public record ObservedPeerHealth(PeerHealthSnapshot Health, TimeSpan Age, DateTimeOffset LastResponseAtUtc)
{
    public HealthResult? Telegram(ConsensusStatus status, Guid leader, int freshSeconds)
    {
        if (Age < TimeSpan.Zero || Age >= TimeSpan.FromSeconds(freshSeconds) || Health.Term != status.Term
            || Health.LeaderNodeId != leader || Health.MembershipVersion != status.MembershipVersion)
        {
            return null;
        }

        HealthResult? result = Health.Components.GetValueOrDefault("Telegram");
        if (result?.Age is not TimeSpan age || age < TimeSpan.Zero || age >= TimeSpan.FromSeconds(90) - Age)
        {
            return null;
        }

        return result with { Age = age + Age };
    }
}
