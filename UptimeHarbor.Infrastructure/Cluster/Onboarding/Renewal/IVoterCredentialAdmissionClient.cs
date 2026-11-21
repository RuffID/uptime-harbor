using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public interface IVoterCredentialAdmissionClient
{
    Task<string> IssueNonceAsync(VoterCredentialNonceCall call, CancellationToken token);
    Task<CommandResult> AuthorizeAsync(VoterCredentialAuthorizeCall call, CancellationToken token);
    Task<CommandResult> BindAsync(VoterCredentialBindCall call, CancellationToken token);
}
