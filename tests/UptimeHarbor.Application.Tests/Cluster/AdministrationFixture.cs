using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Tests.Application.Users;

namespace UptimeHarbor.Tests.Application.Cluster;

public class AdministrationFixture : SharedAccountFixture
{
    public ClusterAdministrationAccess Access(Guid? actor = null)
    {
        User user = new(actor ?? Scenario.Actor);
        return new(new AcceptedAdministratorAccessPolicy(user, State), user, State);
    }

    public ClusterAdministrationQuery Query(Guid? actor = null)
    {
        return new(Access(actor), State, new User(actor ?? Scenario.Actor), TimeProvider.System);
    }

    public ClusterPendingQuery Pending()
    {
        return new(Access(), State, Work, Payloads);
    }

    public ClusterConfigurationCommands Commands(Guid? actor = null)
    {
        User user = new(actor ?? Scenario.Actor);
        AcceptedAdministratorAccessPolicy policy = new(user, State);
        PendingCommandService pending = new(Work, user, policy, State, Payloads, Events, new PendingAdmissionGate());
        return new(Access(actor), State, pending, Work, Payloads, TimeProvider.System);
    }

    public ConfigurationChangeRequest Server(string name = "server")
    {
        return new(Guid.NewGuid(), Guid.NewGuid(), "Server", 0, "Upsert", new()
        {
            Name = name, Endpoint = "https://test.invalid/health"
        });
    }
}
