using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public interface IOnboardingAudit
{
    void Accepted(RevisionImage before, RevisionImage after, RevisionCommand command, CommandResult result);
    void Rejected(Guid operationId, Guid invitationId, Guid actorId);
}
