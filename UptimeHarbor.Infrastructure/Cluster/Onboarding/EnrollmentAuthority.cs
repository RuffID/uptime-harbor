using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Exact capability живёт только в проверенном enrollment вызове; peer batch её не получает.</summary>
public class EnrollmentAuthority : IEnrollmentAuthority
{
    private readonly AsyncLocal<string?> authorized = new();

    internal async Task<CommandResult> AcceptAsync(RevisionDispatcher dispatcher, RevisionCommand command,
        CancellationToken token)
    {
        if (authorized.Value is not null)
        {
            throw new InvalidOperationException("NestedEnrollmentAuthorization");
        }

        authorized.Value = RevisionCodec.Digest(command);
        try
        {
            return await dispatcher.AcceptAsync(command, command.OriginNodeId, token);
        }
        finally
        {
            authorized.Value = null;
        }
    }

    public ValueTask<bool> VerifyAsync(RevisionCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool supported = command.Action is "ReserveInvitation" or "AuthorizeEnrollmentCertificate"
            or "BindEnrollmentCredential" or "AdmitInvitedNode" or "ResumeOnboardingAttempt"
            or "RenewOnboardingAdmission" or "RenewOnboardingProcess";
        return ValueTask.FromResult(supported && authorized.Value == RevisionCodec.Digest(command));
    }
}
