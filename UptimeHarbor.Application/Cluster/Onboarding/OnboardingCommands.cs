using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnboardingCommands
{
    public static bool IsOnboarding(RevisionCommand command)
    {
        return command.Action is "IssueInvitation" or "ReplaceInvitation" or "RevokeInvitation"
            or "ExpireInvitation" or "ReserveInvitation" or "AuthorizeEnrollmentCertificate"
            or "BindEnrollmentCredential" or "AdmitInvitedNode" or "RenewOnboardingAdmission"
            or "ResumeOnboardingAttempt" or "PauseOnboardingAttempt" or "ContinueOnboardingAttempt"
            or "RecordOnboardingProgress" or "CancelOnboardingAttempt" or "RenewOnboardingProcess" or "CompleteOnboardingAttempt"
            or "RequestOnboardingMembership" or "AuthorizeOnboardingMembership" or "ConfirmOnboardingMembership";
    }

    public static bool NeedsEnrollmentAuthority(RevisionCommand command)
    {
        return command.Action is "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential"
            or "AdmitInvitedNode" or "RenewOnboardingAdmission" or "ResumeOnboardingAttempt"
            or "RecordOnboardingProgress" or "ReserveInvitation" or "RenewOnboardingProcess" or "CompleteOnboardingAttempt"
            or "RequestOnboardingMembership" or "AuthorizeOnboardingMembership" or "ConfirmOnboardingMembership";
    }

    public static bool IsCoordinator(RevisionCommand command)
    {
        return command.Action is "ExpireInvitation" or "ReserveInvitation" or "AuthorizeEnrollmentCertificate"
            or "BindEnrollmentCredential" or "AdmitInvitedNode" or "RenewOnboardingAdmission"
            or "ResumeOnboardingAttempt" or "PauseOnboardingAttempt" or "RecordOnboardingProgress"
            or "RenewOnboardingProcess" or "CompleteOnboardingAttempt" or "RequestOnboardingMembership" or "AuthorizeOnboardingMembership" or "ConfirmOnboardingMembership";
    }

    public static void Validate(RevisionCommand command)
    {
        OnboardingCommand data = command.Onboarding ?? throw new InvalidDataException("OnboardingPayloadMissing");
        bool mixed = command.Value is not null || command.Operation is not null || command.Monitoring is not null
            || command.Delivery is not null || command.Target is not null || command.Replacement is not null
            || command.AccountAction is not null || command.ResolvesConflictId.HasValue;
        if (command.Schema is not (2 or 3) || data.Version != 1 || mixed || command.EntityKind != DeletedEntityKind.Node
            || command.EntityId != data.InvitationId || data.ProcessEpoch < 0 || data.ProgressIndex < 0)
        {
            throw new InvalidDataException("OnboardingEnvelopeInvalid");
        }

        StorageGuards.Id(data.StorageGeneration, nameof(data.StorageGeneration));
        StorageGuards.Id(data.InvitationId, nameof(data.InvitationId));
        bool coordinator = IsCoordinator(command);
        if (coordinator)
        {
            if (command.SystemSource != "OnboardingCoordinator" || command.ActorUserId != Guid.Empty || command.ActorRevision != 0)
            {
                throw new InvalidDataException("OnboardingCoordinatorInvalid");
            }
        }
        else
        {
            StorageGuards.Id(command.ActorUserId, nameof(command.ActorUserId));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ActorRevision);
            if (command.SystemSource is not null)
            {
                throw new InvalidDataException("OnboardingActorInvalid");
            }
        }

        bool issue = command.Action is "IssueInvitation" or "ReplaceInvitation";
        bool reserve = command.Action == "ReserveInvitation";
        bool reauthenticate = issue || command.Action is "RevokeInvitation" or "ContinueOnboardingAttempt" or "CancelOnboardingAttempt";
        bool issuance = command.Action is "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential";
        bool admit = command.Action == "AdmitInvitedNode";
        bool process = reserve || command.Action is "ResumeOnboardingAttempt" or "RenewOnboardingAdmission"
            or "RecordOnboardingProgress" or "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential" or "AdmitInvitedNode"
            or "RenewOnboardingProcess" or "CompleteOnboardingAttempt" or "RequestOnboardingMembership" or "AuthorizeOnboardingMembership" or "ConfirmOnboardingMembership";
        bool invitationOnly = issue || command.Action is "RevokeInvitation" or "ExpireInvitation";
        if ((issue || reserve) != (data.Verifier is not null) || reserve != (data.Request is not null)
            || reserve != (data.RequestDigest is not null) || reauthenticate != data.ReauthenticatedAtUtc.HasValue
            || (command.Action == "ReplaceInvitation") != data.ReplacesInvitationId.HasValue
            || issuance != data.IssueId.HasValue || (command.Action == "BindEnrollmentCredential") != (data.CertificateDigest is not null)
            || (command.Action == "AuthorizeEnrollmentCertificate") != (data.Issuance is not null)
            || admit != (data.Node is not null) || admit != (data.Credential is not null)
            || process != (data.BootId != Guid.Empty) || process != (data.ProcessEpoch > 0)
            || invitationOnly == (data.AttemptId != Guid.Empty)
            || (command.Action != "RecordOnboardingProgress" && data.ProgressIndex != 0))
        {
            throw new InvalidDataException("OnboardingFieldsInvalid");
        }

        if (command.Schema == 3 && process)
        {
            if (!IsBootChallenge(data.BootChallenge))
            {
                throw new InvalidDataException("OnboardingBootChallengeInvalid");
            }
        }
        else if (data.BootChallenge is not null)
        {
            throw new InvalidDataException("UnexpectedOnboardingBootChallenge");
        }

        bool confirm = command.Action == "ConfirmOnboardingMembership";
        bool change = confirm || command.Action == "AuthorizeOnboardingMembership";
        if (change != data.MembershipChangeId.HasValue || data.MembershipVersion < 0
            || !confirm && data.MembershipVersion != 0)
        {
            throw new InvalidDataException("OnboardingMembershipFieldsInvalid");
        }

        if (data.Verifier is not null)
        {
            StorageGuards.Digest(data.Verifier);
        }

        if (data.Request is not null)
        {
            EnrollmentRequestCodec.Validate(data.Request);
            bool identityMismatch = data.Request.ClusterId != command.ClusterId || data.Request.Generation != command.ClusterGeneration
                || data.Request.InvitationId != data.InvitationId || data.Request.AttemptId != data.AttemptId;
            if (identityMismatch || EnrollmentRequestCodec.Digest(data.Request) != data.RequestDigest)
            {
                throw new InvalidDataException("EnrollmentRequestMismatch");
            }
        }

        if (data.ReauthenticatedAtUtc.HasValue)
        {
            StorageGuards.Utc(data.ReauthenticatedAtUtc.Value);
        }

        if (data.CoordinatorAtUtc.HasValue)
        {
            StorageGuards.Utc(data.CoordinatorAtUtc.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.CoordinatorTerm);
        }
        else if (data.CoordinatorTerm != 0)
        {
            throw new InvalidDataException("OnboardingTimestampMissing");
        }

        if (data.CertificateDigest is not null)
        {
            StorageGuards.Digest(data.CertificateDigest);
        }

        if (data.Issuance is not null)
        {
            EnrollmentIssuance issuanceData = data.Issuance;
            StorageGuards.Id(issuanceData.IssueId, nameof(issuanceData.IssueId));
            StorageGuards.Id(issuanceData.IssuerEpoch, nameof(issuanceData.IssuerEpoch));
            StorageGuards.Utc(issuanceData.NotBeforeUtc);
            StorageGuards.Utc(issuanceData.NotAfterUtc);
            bool invalidSerial = issuanceData.Serial is null || issuanceData.Serial.Length != 32 || issuanceData.Serial.Any(character => !char.IsAsciiHexDigit(character));
            if (issuanceData.Version != 1 || issuanceData.IssueId != data.IssueId || invalidSerial)
            {
                throw new InvalidDataException("EnrollmentIssuanceInvalid");
            }
        }
    }

    public static bool IsBootChallenge(string? value)
    {
        Span<byte> bytes = stackalloc byte[32];
        return value is not null && value.Length == 44 && Convert.TryFromBase64String(value, bytes, out int length)
            && length == 32 && Convert.ToBase64String(bytes) == value;
    }
}
