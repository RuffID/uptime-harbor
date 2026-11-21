using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Устанавливает уже принятый credential в защищённое локальное хранилище.</summary>
public class LocalVoterCredentialInstallationCoordinator(LocalVoterCredentialBindingCoordinator binding,
    LocalVoterCredentialFiles files, InstallationState installation, ILogger<LocalVoterCredentialInstallationCoordinator> logger)
{
    public async Task<LocalVoterCredentialInstallation> InstallAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            VerifiedInstallation current = await VerifyAsync(reference, token);
            return RequireConfirmed(await CompleteAsync(reference, current, null, token));
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task<LocalVoterCredentialInstallation> ReadInstalledAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            return RequireConfirmed(await VerifyAsync(reference, token));
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    internal async Task<VerifiedInstallation> VerifyAsync(string reference, CancellationToken token,
        ProjectionHead? previous = null, LocalVoterCredentialReplay? replay = null)
    {
        var current = await binding.ReadBoundCoreAsync(reference, token);
        replay?.RequirePendingEvidence(current.Local, current.Image);
        if (replay is not null && current.Binding != replay.Binding)
        {
            throw new InvalidDataException("VoterCredentialReplayedBindChanged");
        }

        return VerifyRecords(reference, current, previous);
    }

    internal VerifiedInstallation VerifyReplayed(string reference, RevisionImage image)
    {
        return VerifyRecords(reference, binding.ReadReplayed(reference, image), null);
    }

    // The startup publication holds Transition through all writes and the TLS swap.
    internal async Task<VerifiedInstallation> CompleteRestartAsync(string reference, LocalVoterCredentialReplay replay,
        CancellationToken token)
    {
        var bound = await binding.RecoverRestartResultAsync(reference, replay, token);
        var current = VerifyRecords(reference, bound, replay.Head);
        return await CompleteAsync(reference, current, replay, token);
    }

    private Task<VerifiedInstallation> CompleteAsync(string reference, VerifiedInstallation current,
        LocalVoterCredentialReplay? replay, CancellationToken token)
    {
        return CompleteRecordsAsync(files, reference, current,
            (floor, cancellationToken) => VerifyAsync(reference, cancellationToken, floor, replay),
            confirmed => logger.LogInformation("VoterCredentialLocalInstallationConfirmed {IssueId} {CredentialRevision}",
                confirmed.IssueId, confirmed.CredentialRevision), token);
    }

    internal static async Task<VerifiedInstallation> CompleteRecordsAsync(LocalVoterCredentialFiles files,
        string reference, VerifiedInstallation current, Func<ProjectionHead, CancellationToken, Task<VerifiedInstallation>> verify,
        Action<LocalVoterCredentialInstallation> logConfirmed, CancellationToken token)
    {
        if (!current.HasManifest)
        {
            files.WriteOnce(reference, LocalVoterCredentialFiles.MANIFEST_STAGE, StrictOnboardingJson.Encode(current.Manifest));
            current = await verify(current.Manifest.AcceptedHead, token);
            if (!current.HasManifest)
            {
                throw new InvalidDataException("VoterCredentialLocalManifestMissing");
            }
        }

        if (!current.HasReference)
        {
            files.WriteOnce(reference, LocalVoterCredentialFiles.REFERENCE_STAGE, StrictOnboardingJson.Encode(current.Reference));
            current = await verify(current.Reference.AcceptedHead, token);
            if (!current.HasReference)
            {
                throw new InvalidDataException("VoterCredentialLocalInstalledReferenceMissing");
            }
        }

        if (!current.HasConfirmation)
        {
            files.WriteOnce(reference, LocalVoterCredentialFiles.CONFIRMATION_STAGE, StrictOnboardingJson.Encode(current.Confirmation));
            current = await verify(current.Confirmation.AcceptedHead, token);
            logConfirmed(RequireConfirmed(current));
        }

        return current;
    }

    private VerifiedInstallation VerifyRecords(string reference,
        (LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image) evidence,
        ProjectionHead? previous)
    {
        var (local, accepted, image) = evidence;
        LocalVoterCredentialAuthorization authorization = local.Authorization
            ?? throw new InvalidDataException("VoterCredentialLocalAuthorizationMissing");
        return VerifyRecords(files, reference, local, accepted, image, Digest(authorization), previous);
    }

    internal static VerifiedInstallation VerifyRecords(LocalVoterCredentialFiles files, string reference,
        LocalVoterCredentialState local, LocalVoterCredentialBinding accepted, RevisionImage image,
        string authorizationDigest, ProjectionHead? previous, ProjectionHead? acceptedHead = null)
    {
        ProjectionHead head = acceptedHead ?? new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);
        if (previous is not null)
        {
            LocalVoterCredentialReplay.RequireAtLeast(head, previous);
        }

        LocalVoterCredentialManifest? manifest = Read<LocalVoterCredentialManifest>(
            files, reference, LocalVoterCredentialFiles.MANIFEST_STAGE);
        LocalVoterCredentialInstalledReference? installed = Read<LocalVoterCredentialInstalledReference>(
            files, reference, LocalVoterCredentialFiles.REFERENCE_STAGE);
        LocalVoterCredentialConfirmation? confirmation = Read<LocalVoterCredentialConfirmation>(
            files, reference, LocalVoterCredentialFiles.CONFIRMATION_STAGE);
        if ((manifest is null && installed is not null) || (installed is null && confirmation is not null))
        {
            throw new InvalidDataException("VoterCredentialLocalInstallationPrefixMissing");
        }

        EnrollmentIssuance template = local.Intent.Request.Renewal.Issuance;
        EnrollmentIssuedArtifact artifact = local.Artifact ?? throw new InvalidDataException("VoterCredentialLocalArtifactMissing");
        LocalVoterCredentialManifest expectedManifest = new(1, reference, Digest(local.Intent),
            authorizationDigest, template.CsrDigest, template.SpkiDigest, artifact.TemplateDigest,
            artifact.CertificateDigest, Digest(accepted), template.IssueId, accepted.CredentialRevision, manifest?.AcceptedHead ?? head);
        LocalVoterCredentialInstalledReference expectedReference = new(1, Digest(expectedManifest), template.IssueId,
            accepted.CredentialRevision, installed?.AcceptedHead ?? head);
        LocalVoterCredentialConfirmation expectedConfirmation = new(1, Digest(expectedReference),
            confirmation?.AcceptedHead ?? head);

        if ((manifest is not null && manifest != expectedManifest)
            || (installed is not null && installed != expectedReference)
            || (confirmation is not null && confirmation != expectedConfirmation))
        {
            throw new InvalidDataException("VoterCredentialLocalInstallationChanged");
        }

        LocalVoterCredentialReplay.RequireAtLeast(expectedReference.AcceptedHead, expectedManifest.AcceptedHead);
        LocalVoterCredentialReplay.RequireAtLeast(expectedConfirmation.AcceptedHead, expectedReference.AcceptedHead);
        LocalVoterCredentialReplay.RequireAtLeast(head, expectedConfirmation.AcceptedHead);

        return new(expectedManifest, expectedReference, expectedConfirmation,
            manifest is not null, installed is not null, confirmation is not null, local, accepted, image);
    }

    internal static T? Read<T>(LocalVoterCredentialFiles files, string reference, string stage) where T : class
    {
        byte[]? bytes = files.Read(reference, stage);
        if (bytes is null)
        {
            return null;
        }

        return StrictOnboardingJson.Decode<T>(bytes);
    }

    internal static string Digest<T>(T value)
    {
        return EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(value));
    }

    internal static LocalVoterCredentialInstallation RequireConfirmed(VerifiedInstallation current)
    {
        if (!current.HasConfirmation)
        {
            throw new InvalidDataException("VoterCredentialLocalInstallationUnconfirmed");
        }

        LocalVoterCredentialManifest manifest = current.Manifest;
        return new(manifest.MaterialReference, manifest.IssueId, manifest.CredentialRevision, manifest.CertificateFingerprint);
    }

    internal sealed record VerifiedInstallation(LocalVoterCredentialManifest Manifest, LocalVoterCredentialInstalledReference Reference,
        LocalVoterCredentialConfirmation Confirmation, bool HasManifest, bool HasReference, bool HasConfirmation,
        LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image);
}
