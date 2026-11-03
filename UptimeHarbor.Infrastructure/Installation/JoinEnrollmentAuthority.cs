using System.Security.Cryptography;
using System.Text;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Действительный E1–E3 по browser TLS. Не запускает snapshot, membership, native runtime или общий доступ.</summary>
public class JoinEnrollmentAuthority(IOnboardingProgressUnitOfWork work, EnrollmentTransport transport,
    LocalSecretStore secrets, OnboardingProgress progress, InstallationState installation,
    InstallationLease lease, EnrollmentEndpoints endpoints, TimeProvider time, EnrollmentTrustInstallation? trust = null,
    EnrollmentPeerProbes? probes = null)
{
    public async Task<ServiceResult<SetupAuthorityEvidence>> PrepareAsync(LocalOnboardingAttempt input, CancellationToken token)
    {
        LocalInstallationProfile profile = installation.Current ?? throw new InvalidOperationException("InstallationProfileMissing");
        if (profile.Mode != "Join" || profile.Phase != InstallationPhase.LocalPrepared || profile.AttemptId != input.AttemptId
            || profile.BootId != lease.BootId || input.BrowserOrigin is null || input.CsrDer is null)
        {
            return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "EnrollmentLocalIntentRequired");
        }

        LocalOnboardingAttempt local = await work.Attempts.FindAsync(input.AttemptId, token)
            ?? throw new InvalidDataException("EnrollmentLocalIntentMissing");
        Uri origin = endpoints.Origin(local.BrowserOrigin!);
        byte[] pem = secrets.Read(local.AttemptId, "peer", local.PrivateKeyReference);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            local = await ReadStateAsync(local, origin, key, token);
            if (!local.ReservationIndex.HasValue && local.Descriptor is null)
            {
                local = await StepAsync(local, origin, key, "ReserveInvitation", EnrollmentEndpoint.Reserve, token);
            }

            if (!local.ReservationIndex.HasValue && local.Descriptor is not null)
            {
                await progress.ReservedAsync(local.AttemptId, local.Descriptor!.ReservationIndex, local.Revision, token);
                local = await work.Attempts.FindAsync(local.AttemptId, token)
                    ?? throw new InvalidDataException("EnrollmentLocalIntentMissing");
            }

            if (local.Descriptor?.BootId != lease.BootId || local.Descriptor.BootChallenge != lease.BootChallenge)
            {
                if (local.Descriptor!.ProcessLeaseUntilUtc > time.GetUtcNow())
                {
                    return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "PreviousProcessLeaseActive");
                }

                // Resume той же identity имеет отдельный accepted ProcessEpoch; invitation уже удалён.
                local = await StepAsync(local, origin, key, "ResumeOnboardingAttempt", EnrollmentEndpoint.Resume, token);
            }

            if (local.Descriptor!.ProcessLeaseUntilUtc - time.GetUtcNow() <= TimeSpan.FromMinutes(1))
            {
                local = await StepAsync(local, origin, key, "RenewOnboardingProcess", EnrollmentEndpoint.Renew, token);
            }

            if (local.Descriptor?.Issuance is null)
            {
                local = await StepAsync(local, origin, key, "AuthorizeEnrollmentCertificate", EnrollmentEndpoint.Issue, token);
            }

            if (local.Descriptor!.Status == "CertificateAuthorized")
            {
                local = await StepAsync(local, origin, key, "BindEnrollmentCredential", EnrollmentEndpoint.Issue, token);
            }

            if (!local.Descriptor!.AdmissionUntilUtc.HasValue)
            {
                local = await StepAsync(local, origin, key, "AdmitInvitedNode", EnrollmentEndpoint.Admit, token);
            }

            else if (local.Descriptor.AdmissionUntilUtc - time.GetUtcNow() < TimeSpan.FromMinutes(3))
            {
                local = await StepAsync(local, origin, key, "RenewOnboardingAdmission", EnrollmentEndpoint.Renew, token);
            }

            if (trust is null)
            {
                return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "EnrollmentTrustInstallerRequired");
            }

            local = await trust.InstallAsync(local, token);
            if (probes is null)
            {
                return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "EnrollmentPeerProbesRequired");
            }

            await probes.ForwardAsync(local, trust, token);
            local = await ProbeReverseAsync(local, origin, key, token);
            return ServiceResult<SetupAuthorityEvidence>.Ok(new(local.AttemptId, profile.RuntimeGeneration,
                local.ReservationIndex!.Value, local.RequestDigest, SetupAuthorityEvidenceKind.ReservationAccepted)
            {
                Enrollment = local.Descriptor, TrustReference = local.TrustReference
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    private async Task<LocalOnboardingAttempt> ProbeReverseAsync(LocalOnboardingAttempt local, Uri origin, ECDsa key, CancellationToken token)
    {
        const string ACTION = "ProbeEnrollmentPeers";
        EnrollmentDescriptor accepted = local.Descriptor!;
        EnrollmentNonceRequest request = new(1, local.AttemptId, lease.BootId, lease.BootChallenge, accepted.ProcessEpoch,
            ACTION, local.RequestDigest);
        string nonce = await transport.PostAsync<EnrollmentNonceRequest, string>(origin, EnrollmentEndpoint.Nonce, request, token);
        EnrollmentProof proof = EnrollmentProofs.Sign(local.Request,
            new(1, Guid.NewGuid(), ACTION, lease.BootId, lease.BootChallenge, accepted.ProcessEpoch, nonce, local.RequestDigest, string.Empty), key);
        EnrollmentDescriptor descriptor = await transport.PostAsync<EnrollmentExchange, EnrollmentDescriptor>(origin,
            EnrollmentEndpoint.Probe, new(1, local.Request, proof, local.CsrDer!, null), token);
        EnrollmentDescriptorValidation.Bound(descriptor, local, proof, endpoints, time.GetUtcNow());
        DateTimeOffset expires = new[] { time.GetUtcNow().AddMinutes(2), descriptor.ProcessLeaseUntilUtc,
            descriptor.AdmissionUntilUtc!.Value }.Min();
        LocalOnboardingAttempt next = local with
        {
            Revision = local.Revision + 1, Descriptor = descriptor,
            PeerProbes = new(lease.BootId, lease.BootChallenge, descriptor.ProcessEpoch, local.RequestDigest, expires)
        };
        await work.Attempts.ReplaceAsync(next, local.Revision, token);
        await work.SaveChangesAsync(token);
        return next;
    }

    private async Task<LocalOnboardingAttempt> StepAsync(LocalOnboardingAttempt local, Uri origin,
        ECDsa key, string action, EnrollmentEndpoint endpoint, CancellationToken token)
    {
        long epoch = local.Descriptor?.ProcessEpoch ?? 1;
        if (action == "ResumeOnboardingAttempt")
        {
            epoch++;
        }

        Guid operation;
        if (action is "ResumeOnboardingAttempt" or "RenewOnboardingProcess" or "RenewOnboardingAdmission")
        {
            string predecessor = local.Descriptor?.ProcessLeaseUntilUtc.ToString("O") ?? string.Empty;
            if (action == "RenewOnboardingAdmission")
            {
                predecessor = local.Descriptor?.AdmissionUntilUtc?.ToString("O") ?? string.Empty;
            }

            string identity = FormattableString.Invariant($"EnrollmentProcess/v1/{action}/{local.AttemptId:D}/{lease.BootId:D}/{lease.BootChallenge}/{epoch}/{predecessor}");
            operation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
        }
        else
        {
            operation = local.OperationIds[action];
        }

        EnrollmentNonceRequest nonceRequest = new(1, local.AttemptId, lease.BootId, lease.BootChallenge, epoch, action, local.RequestDigest);
        string nonce = await transport.PostAsync<EnrollmentNonceRequest, string>(origin, EnrollmentEndpoint.Nonce, nonceRequest, token);
        EnrollmentProof proof = EnrollmentProofs.Sign(local.Request,
            new(1, operation, action, lease.BootId, lease.BootChallenge, epoch, nonce, local.RequestDigest, string.Empty), key);
        string? invitation = null;
        if (action == "ReserveInvitation")
        {
            byte[] plaintext = secrets.ReadInvitation(local.AttemptId, local.ProtectedInvitation
                ?? throw new InvalidDataException("EnrollmentInvitationMissing"));
            try
            {
                invitation = Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        EnrollmentExchange request = new(1, local.Request, proof, local.CsrDer!, invitation);
        EnrollmentDescriptor descriptor = await transport.PostAsync<EnrollmentExchange, EnrollmentDescriptor>(origin, endpoint, request, token);
        EnrollmentDescriptorValidation.Bound(descriptor, local, proof, endpoints, time.GetUtcNow());
        LocalOnboardingAttempt next = local with { Revision = local.Revision + 1, Descriptor = descriptor };
        await work.Attempts.ReplaceAsync(next, local.Revision, token);
        await work.SaveChangesAsync(token);
        return next;
    }

    private async Task<LocalOnboardingAttempt> ReadStateAsync(LocalOnboardingAttempt local, Uri origin, ECDsa key, CancellationToken token)
    {
        const string ACTION = "ReadEnrollmentAttempt";
        long epoch = local.Descriptor?.ProcessEpoch ?? 1;
        EnrollmentNonceRequest request = new(1, local.AttemptId, lease.BootId, lease.BootChallenge, epoch, ACTION, local.RequestDigest);
        string nonce = await transport.PostAsync<EnrollmentNonceRequest, string>(origin, EnrollmentEndpoint.Nonce, request, token);
        EnrollmentProof proof = EnrollmentProofs.Sign(local.Request,
            new(1, Guid.NewGuid(), ACTION, lease.BootId, lease.BootChallenge, epoch, nonce, local.RequestDigest, string.Empty), key);
        EnrollmentExchange exchange = new(1, local.Request, proof, local.CsrDer!, null);
        EnrollmentState state = await transport.PostAsync<EnrollmentExchange, EnrollmentState>(origin, EnrollmentEndpoint.State, exchange, token);
        if (state.Version != 1 || state.Request != local.Request || state.ResponseProof != proof)
        {
            throw new InvalidDataException("EnrollmentStateBindingInvalid");
        }

        if (state.Descriptor is null)
        {
            if (local.ReservationIndex.HasValue || local.Descriptor is not null)
            {
                throw new InvalidDataException("EnrollmentAcceptedReservationLost");
            }

            return local;
        }

        EnrollmentDescriptorValidation.Bound(state.Descriptor, local, proof, endpoints, time.GetUtcNow());
        LocalOnboardingAttempt next = local with { Descriptor = state.Descriptor, Revision = local.Revision + 1 };
        await work.Attempts.ReplaceAsync(next, local.Revision, token);
        await work.SaveChangesAsync(token);
        return next;
    }
}
