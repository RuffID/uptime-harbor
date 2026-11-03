using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Installation;

/// <summary>Durable local intent, без accepted index и без private material.</summary>
public record PreparedGenesis(GenesisAuthorization Authorization, RevisionCommand InitializeCluster,
    EnrollmentRequest Request, EnrollmentIssuance Template, byte[] CsrDer)
{
    public override string ToString()
    {
        return $"PreparedGenesis {Authorization.GenesisId}";
    }
}
