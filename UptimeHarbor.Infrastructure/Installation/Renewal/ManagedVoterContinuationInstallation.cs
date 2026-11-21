using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using static UptimeHarbor.Infrastructure.Installation.Renewal.LocalVoterCredentialInstallationCoordinator;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Установка под Transition с полномочиями из проверенного среза одной операции.</summary>
public class ManagedVoterContinuationInstallation(LocalVoterCredentialFiles files,
    LocalVoterContinuationInstallation local, DynamicPeerTls tls, TimeProvider time,
    ILogger<ManagedVoterContinuationInstallation> logger)
{
    internal string? ReadPinnedBindLocked(LocalVoterCredentialIntent intent, ManagedContinuationCut cut, CancellationToken token)
    {
        string reference = intent.KeyReference;
        var pin = Read<LocalVoterContinuationInstallation.BindIntent>(files, reference,
            LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT);
        if (cut.Slice.Bound is not null || files.HasInstallationRecords(reference)
            || files.Read(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT) is not null)
        {
            throw new InvalidDataException("VoterContinuationStartupUnexpectedBind");
        }

        CommandResult registration = RequireRegistration(cut, intent, token);
        if (pin is null)
        {
            return null;
        }

        LocalVoterCredentialReplay.RequireAtLeast(cut.Slice.Head, pin.Head);
        RequireArtifact(intent, cut, registration, pin.Artifact);
        if (pin.MaterialDigest != Digest(intent) || pin.Registration != registration
            || pin.Head.Index < registration.Index || pin.Head.Term < registration.Term)
        {
            throw new InvalidDataException("VoterContinuationLocalBindIntentChanged");
        }

        cut.RequireCurrent(token);
        return pin.Artifact.CertificateDigest;
    }

    internal string PinBindLocked(LocalVoterCredentialIntent intent, ManagedContinuationCut cut,
        VoterContinuationWire.IssuedLeaf receipt, CancellationToken token)
    {
        string reference = intent.KeyReference;
        var slice = cut.Slice;
        CommandResult registration = RequireRegistration(cut, intent, token);
        if (slice.Bound is not null || files.HasInstallationRecords(reference)
            || files.Read(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT) is not null)
        {
            throw new InvalidDataException("VoterContinuationStartupUnexpectedBind");
        }

        EnrollmentIssuance leaf = intent.Request.Renewal.Issuance;
        EnrollmentIssuedArtifact artifact = new(receipt.IssueId, leaf.IssuerEpoch, receipt.OperationId,
            VoterContinuationAuthority.IssuanceDigest(intent.Request, registration.Digest),
            EnrollmentSigningJournal.TemplateDigest(leaf), null, registration.Index,
            receipt.CertificateDer, receipt.CertificateDigest);
        RequireArtifact(intent, cut, registration, artifact);
        var saved = Read<LocalVoterContinuationInstallation.BindIntent>(files, reference,
            LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT);
        var pin = new LocalVoterContinuationInstallation.BindIntent(Digest(intent), registration, artifact, saved?.Head ?? slice.Head);
        LocalVoterCredentialReplay.RequireAtLeast(slice.Head, pin.Head);
        cut.RequireCurrent(token);
        files.WriteOnce(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT, StrictOnboardingJson.Encode(pin));
        cut.RequireCurrent(token);
        return artifact.CertificateDigest;
    }

    internal async Task PublishLockedAsync(LocalInstallationProfile profile, LocalVoterCredentialIntent intent,
        ManagedContinuationCut cut, Func<CancellationToken, Task<ManagedContinuationCut>> read, CancellationToken token)
    {
        string reference = intent.KeyReference;
        if (cut.Slice.Registered is null || cut.Slice.Process is not { CredentialContinuation: true } process
            || process.BootId != cut.Replay.Process.BootId || process.BootChallenge != cut.Replay.Process.BootChallenge)
        {
            throw new InvalidDataException("VoterContinuationStartupRegistrationRequired");
        }

        var pin = RequireBindPin(intent);
        var (saved, wrote) = PinAcceptedBindLocked(intent, cut, pin, token);
        if (wrote)
        {
            cut = await read(token);
        }

        VerifiedInstallation verified = Verify(cut.Slice.Head, token);
        verified = await CompleteRecordsAsync(files, reference, verified, RefreshAndVerifyAsync, confirmed =>
            logger.LogInformation("VoterContinuationLocalInstallationConfirmed {IssueId} {CredentialRevision}",
                confirmed.IssueId, confirmed.CredentialRevision), token);
        verified = await RefreshAndVerifyAsync(verified.Confirmation.AcceptedHead, token);
        var installed = RequireConfirmed(verified);
        var (savedIntent, key) = local.OpenIntent(reference);
        using (key)
        {
            if (savedIntent != intent)
            {
                throw new InvalidDataException("VoterContinuationLocalIntentChanged");
            }

            var slice = cut.Slice;
            LocalVoterContinuationQuarantine quarantine = new(reference, slice.CredentialBinding!.Command, slice.Process!);
            using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(pin.Artifact.CertificateDer);
            X509Certificate2 certificate = publicLeaf.CopyWithPrivateKey(key);
            X509Certificate2? issuer = null;
            try
            {
                issuer = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(slice.Issuer.CertificateDer));
                cut.RequireCurrent(token);
                tls.PublishContinuationBeforeNativeStart(profile, cut, quarantine, installed, certificate, issuer);
            }
            catch
            {
                certificate.Dispose();
                issuer?.Dispose();
                throw;
            }
        }

        async Task<VerifiedInstallation> RefreshAndVerifyAsync(ProjectionHead floor, CancellationToken cancellationToken)
        {
            cut = await read(cancellationToken);
            return Verify(floor, cancellationToken);
        }

        VerifiedInstallation Verify(ProjectionHead floor, CancellationToken cancellationToken)
        {
            LocalVoterCredentialBinding current = VerifyBind(intent, cut, pin, cancellationToken);
            LocalVoterCredentialReplay.RequireAtLeast(cut.Slice.Head, saved.Head);
            LocalVoterCredentialReplay.RequireAtLeast(saved.Head, pin.Head);
            if (saved.Binding != current || saved.Head.Index < current.CredentialRevision || saved.Head.Term < current.Result.Term
                || Read<LocalVoterContinuationInstallation.BindResult>(files, reference,
                    LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT) != saved
                || Digest(Read<LocalVoterContinuationInstallation.BindIntent>(files, reference,
                    LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT)) != Digest(pin))
            {
                throw new InvalidDataException("VoterContinuationLocalBindResultChanged");
            }

            var records = VerifyBoundRecords(intent, cut, pin, saved, floor);
            cut.RequireCurrent(cancellationToken);
            return records;
        }
    }

    internal LocalVoterContinuationInstallation.BindIntent RequireBindPin(LocalVoterCredentialIntent intent)
    {
        return Read<LocalVoterContinuationInstallation.BindIntent>(files, intent.KeyReference,
            LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT)
            ?? throw new InvalidDataException("VoterContinuationLocalBindIntentMissing");
    }

    internal (LocalVoterContinuationInstallation.BindResult Result, bool Wrote) PinAcceptedBindLocked(
        LocalVoterCredentialIntent intent, ManagedContinuationCut cut, LocalVoterContinuationInstallation.BindIntent pin,
        CancellationToken token)
    {
        string reference = intent.KeyReference;
        var current = VerifyBind(intent, cut, pin, token);
        var saved = Read<LocalVoterContinuationInstallation.BindResult>(files, reference,
            LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT);
        if (saved is not null)
        {
            LocalVoterCredentialReplay.RequireAtLeast(cut.Slice.Head, saved.Head);
            LocalVoterCredentialReplay.RequireAtLeast(saved.Head, pin.Head);
            if (saved.Binding != current || saved.Head.Index < current.CredentialRevision || saved.Head.Term < current.Result.Term)
            {
                throw new InvalidDataException("VoterContinuationLocalBindResultChanged");
            }

            VerifyBoundRecords(intent, cut, pin, saved, saved.Head);
            cut.RequireCurrent(token);
            return (saved, false);
        }

        if (files.HasInstallationRecords(reference))
        {
            throw new InvalidDataException("VoterContinuationLocalBindResultMissing");
        }

        saved = new(current, cut.Slice.Head);
        cut.RequireCurrent(token);
        files.WriteOnce(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT, StrictOnboardingJson.Encode(saved));
        cut.RequireCurrent(token);
        return (saved, true);
    }

    private VerifiedInstallation VerifyBoundRecords(LocalVoterCredentialIntent intent, ManagedContinuationCut cut,
        LocalVoterContinuationInstallation.BindIntent pin, LocalVoterContinuationInstallation.BindResult saved, ProjectionHead floor)
    {
        var material = new LocalVoterCredentialState(intent, null, pin.Artifact);
        var records = VerifyRecords(files, intent.KeyReference, material, saved.Binding, cut.Replay.Image,
            Digest(pin.Registration), floor, acceptedHead: cut.Slice.Head);
        LocalVoterCredentialReplay.RequireAtLeast(records.Manifest.AcceptedHead, saved.Head);
        return records;
    }

    private LocalVoterCredentialBinding VerifyBind(LocalVoterCredentialIntent intent, ManagedContinuationCut cut,
        LocalVoterContinuationInstallation.BindIntent pin, CancellationToken token)
    {
        cut.RequireCurrent(token);
        var registered = cut.Slice.IssuingRegistration
            ?? throw new InvalidDataException("VoterContinuationLocalExactRegistrationRequired");
        CommandResult registration = registered.Decision;
        var bound = cut.Slice.CredentialBinding ?? throw new InvalidDataException("VoterContinuationLocalExactBindRequired");
        var data = bound.Command.VoterContinuation!;
        LocalVoterCredentialReplay.RequireAtLeast(cut.Slice.Head, pin.Head);
        RequireArtifact(intent, cut, registration, pin.Artifact);
        if (registered.Command.VoterContinuation!.Context.Request != intent.Request
            || pin.Registration != registration || pin.MaterialDigest != Digest(intent)
            || pin.Head.Index < registration.Index || pin.Head.Term < registration.Term
            || pin.Head.Index >= bound.Decision.Index
            || data.Credential!.Fingerprint != pin.Artifact.CertificateDigest
            || data.Credential.PublicCertificate != Convert.ToBase64String(pin.Artifact.CertificateDer))
        {
            throw new InvalidDataException("VoterContinuationLocalBindReceiptChanged");
        }

        return new(Digest(pin), bound.Decision, data.Acceptance!.AtUtc, cut.Slice.Previous.ExpiresAtUtc);
    }

    private CommandResult RequireRegistration(ManagedContinuationCut cut, LocalVoterCredentialIntent intent, CancellationToken token)
    {
        cut.RequireCurrent(token);
        var registered = cut.Slice.Registered ?? throw new InvalidDataException("VoterContinuationLocalExactRegistrationRequired");
        var (saved, key) = local.OpenIntent(intent.KeyReference);
        using (key)
        {
            if (saved != intent || registered.Command.VoterContinuation!.Context.Request != intent.Request
                || cut.Slice.Process is not { CredentialContinuation: true } process
                || process.BootId != intent.Process!.BootId || process.BootChallenge != intent.Process.BootChallenge)
            {
                throw new InvalidDataException("VoterContinuationLocalRegistrationChanged");
            }
        }

        return registered.Decision;
    }

    private void RequireArtifact(LocalVoterCredentialIntent intent, ManagedContinuationCut cut, CommandResult registration,
        EnrollmentIssuedArtifact artifact)
    {
        VoterCredentialArtifactValidation.RequireIssued(cut.Slice.Issuer, intent.Request, artifact,
            VoterContinuationAuthority.IssuanceDigest(intent.Request, registration.Digest), registration.Index, time.GetUtcNow());
    }
}
