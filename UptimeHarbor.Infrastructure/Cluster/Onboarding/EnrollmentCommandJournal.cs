using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>До consensus сохраняет точные байты запроса, включая serial/lifetime и исходный OperationId.</summary>
public class EnrollmentCommandJournal(ILocalSetupUnitOfWork work, InstallationState installation, TimeProvider time)
{
    public async Task<RevisionCommand> PrepareAsync(Guid operationId, Func<RevisionCommand> prepare, CancellationToken token)
    {
        LocalInstallationProfile profile = installation.Current
            ?? throw new InvalidOperationException("EnrollmentInstallationUnavailable");
        if (profile.Phase != InstallationPhase.ClusterReady || !installation.CommonOpen || !profile.AttemptId.HasValue)
        {
            throw new InvalidOperationException("EnrollmentInstallationNotReady");
        }

        LocalAttemptStep? previous = await work.Profiles.FindStepAsync(operationId, token);
        if (previous is not null)
        {
            RevisionCommand original = JsonSerializer.Deserialize<RevisionCommand>(previous.Evidence
                ?? throw new InvalidDataException("EnrollmentCommandIntentMissing"))
                ?? throw new InvalidDataException("EnrollmentCommandIntentInvalid");
            LocalCommandStepValidation.Require(profile, previous, original, "Enrollment/");
            return original;
        }

        RevisionCommand command = prepare();
        if (command.OperationId != operationId || !OnboardingCommandsSupported(command))
        {
            throw new InvalidDataException("EnrollmentCommandIntentInvalid");
        }

        LocalAttemptStep step = new(operationId, profile.AttemptId.Value, "Enrollment/" + command.Action,
            RevisionCodec.Digest(command), "AcceptedEnrollment", profile.ProfileRevision,
            profile.RuntimeGeneration, time.GetUtcNow(), JsonSerializer.Serialize(command));
        await work.ExecuteInTransactionAsync(_ =>
        {
            work.Profiles.AddStep(step);
            return Task.CompletedTask;
        }, token);
        return command;
    }

    private static bool OnboardingCommandsSupported(RevisionCommand command)
    {
        return command.Action is "ReserveInvitation" or "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential"
            or "AdmitInvitedNode" or "ResumeOnboardingAttempt" or "RenewOnboardingAdmission" or "RenewOnboardingProcess";
    }
}
