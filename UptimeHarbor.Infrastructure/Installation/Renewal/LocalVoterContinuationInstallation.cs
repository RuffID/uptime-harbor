using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using static UptimeHarbor.Infrastructure.Installation.Renewal.LocalVoterCredentialInstallationCoordinator;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Protected local installation of an accepted continuation; has no issuance, Bind or publication ports.</summary>
public sealed class LocalVoterContinuationInstallation(LocalVoterCredentialFiles files, LocalSecretStore secrets,
    InstallationLease lease, InstallationState installation, VoterCredentialRenewalAuthority authority,
    TimeProvider time, ILogger<LocalVoterContinuationInstallation> logger)
{
    public static string Reference(Guid installIdentity, VoterCredentialRequest request)
    {
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        return LocalVoterCredentialMaterial.Reference(installIdentity, request.ClusterId, request.Generation, leaf.NodeId,
            leaf.StorageGeneration, request.Renewal.PreviousCredentialId, continuation: true);
    }

    public async Task<LocalVoterCredentialIntent> PinIntentAsync(VoterCredentialRequest request, byte[] csrDer,
        ECDsa key, Guid previousKeyOwner, string previousKeyReference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            string reference = Reference(lease.InstallIdentity, request);
            LocalVoterCredentialIntent? previous = files.HasSlot(reference) ? ReadIntent(reference) : null;
            LocalVoterCredentialIntent intent = new(1, lease.InstallIdentity, reference, request,
                Convert.ToBase64String(csrDer), previousKeyOwner, previousKeyReference)
            {
                Process = previous?.Process ?? new(lease.BootId, lease.BootChallenge)
            };
            byte[] bundle = LocalVoterCredentialMaterial.Encode(intent, key);
            try
            {
                Decode(reference, bundle);
                RevisionImage image = await authority.ReadBindingAsync(request, token);
                if (previous is null)
                {
                    VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, request.Renewal.Issuance.NodeId);
                    if (image.Results.ContainsKey(request.OperationId)
                        || image.Operations.Credentials.ContainsKey(request.Renewal.Issuance.IssueId)
                        || (latest?.CredentialRevision ?? 0) != request.Renewal.PreviousCredentialRevision)
                    {
                        throw new InvalidDataException("VoterContinuationLocalOriginalIntentRequired");
                    }

                    RequireCurrent(image, token);
                    files.WriteOnce(reference, "intent", bundle);
                }

                if (ReadIntent(reference) != intent)
                {
                    throw new InvalidDataException("VoterContinuationLocalIntentChanged");
                }

                RequireCurrent(image, token);
                return intent;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle);
            }
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task PinBindIntentAsync(string reference, CommandResult registration, byte[] certificateDer,
        CancellationToken token)
    {
        byte[] der = certificateDer.ToArray();
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialIntent intent = ReadIntent(reference);
            RevisionImage image = await authority.ReadBindingAsync(intent.Request, token);
            AcceptedVoterContinuation accepted = RequireRegistration(image, intent, registration);
            if (accepted.Bind is not null || files.Read(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT) is not null
                || files.HasInstallationRecords(reference))
            {
                throw new InvalidDataException("VoterContinuationLocalBindAlreadyStarted");
            }

            DateTimeOffset now = time.GetUtcNow();
            string? refusal = VoterContinuationReducer.Refusal(image, accepted.Registration!, now, image.Index, replay: true);
            if (refusal is not null)
            {
                throw new UnauthorizedAccessException(refusal);
            }

            EnrollmentIssuance leaf = intent.Request.Renewal.Issuance;
            EnrollmentIssuedArtifact artifact = new(leaf.IssueId, leaf.IssuerEpoch, intent.Request.OperationId,
                VoterContinuationAuthority.IssuanceDigest(intent.Request, registration.Digest),
                EnrollmentSigningJournal.TemplateDigest(leaf), null, registration.Index, der, EnrollmentCertificates.Hash(der));
            RequireArtifact(image, intent, registration, artifact, now);
            BindIntent? existing = Read<BindIntent>(files, reference, LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT);
            BindIntent pin = new(Digest(intent), registration, artifact, existing?.Head ?? Head(image));
            LocalVoterCredentialReplay.RequireAtLeast(Head(image), pin.Head);
            RequireCurrent(image, token);
            files.WriteOnce(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT, StrictOnboardingJson.Encode(pin));
            if (ReadIntent(reference) != intent || Digest(ReadBindIntent(reference)) != Digest(pin))
            {
                throw new InvalidDataException("VoterContinuationLocalBindIntentChanged");
            }

            RequireCurrent(image, token);
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public Task<LocalVoterCredentialInstallation> InstallAsync(string reference, BindReceipt response, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ReconcileAsync(reference, response, complete: true, token);
    }

    public Task<LocalVoterCredentialInstallation> RecoverAsync(string reference, CancellationToken token)
    {
        return ReconcileAsync(reference, null, complete: true, token);
    }

    public Task<LocalVoterCredentialInstallation> ReadInstalledAsync(string reference, CancellationToken token)
    {
        return ReconcileAsync(reference, null, complete: false, token);
    }

    private async Task<LocalVoterCredentialInstallation> ReconcileAsync(string reference, BindReceipt? response,
        bool complete, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            return RequireConfirmed(await VerifyAsync(reference, response, complete, token));
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    // Вызывается под InstallationState.Transition, в том числе из атомарной публикации.
    internal async Task<VerifiedInstallation> VerifyAsync(string reference, BindReceipt? response, bool complete,
        CancellationToken token)
    {
        var current = await ReadAcceptedAsync(reference, response, token);
        BindResult? saved = Read<BindResult>(files, reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT);
        if (saved is null)
        {
            if (!complete || files.HasInstallationRecords(reference))
            {
                throw new InvalidDataException("VoterContinuationLocalBindResultMissing");
            }

            RequireCurrent(current.Image, token);
            saved = new(current.Binding, Head(current.Image));
            files.WriteOnce(reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT, StrictOnboardingJson.Encode(saved));
        }

        VerifiedInstallation verified = await VerifyRecordsAsync(Head(current.Image), token);
        if (complete)
        {
            verified = await CompleteRecordsAsync(files, reference, verified, VerifyRecordsAsync,
                confirmed => logger.LogInformation("VoterContinuationLocalInstallationConfirmed {IssueId} {CredentialRevision}",
                    confirmed.IssueId, confirmed.CredentialRevision), token);
        }

        RequireCurrent(verified.Image, token);
        return verified;

        async Task<VerifiedInstallation> VerifyRecordsAsync(ProjectionHead floor, CancellationToken cancellationToken)
        {
            var evidence = await ReadAcceptedAsync(reference, response, cancellationToken);
            ProjectionHead head = Head(evidence.Image);
            LocalVoterCredentialReplay.RequireAtLeast(head, saved.Head);
            LocalVoterCredentialReplay.RequireAtLeast(head, floor);
            if (saved.Head.Index < saved.Binding.CredentialRevision || saved.Head.Term < saved.Binding.Result.Term
                || saved.Binding != evidence.Binding
                || Read<BindResult>(files, reference, LocalVoterCredentialFiles.CONTINUATION_BIND_RESULT) != saved)
            {
                throw new InvalidDataException("VoterContinuationLocalBindResultChanged");
            }

            VerifiedInstallation records = VerifyRecords(files, reference, evidence.Local, evidence.Binding, evidence.Image,
                Digest(evidence.Registration), floor);
            LocalVoterCredentialReplay.RequireAtLeast(records.Manifest.AcceptedHead, saved.Head);
            RequireCurrent(evidence.Image, cancellationToken);
            return records;
        }
    }

    private async Task<(LocalVoterCredentialState Local, CommandResult Registration, LocalVoterCredentialBinding Binding,
        RevisionImage Image)> ReadAcceptedAsync(string reference, BindReceipt? response, CancellationToken token)
    {
        LocalVoterCredentialIntent intent = ReadIntent(reference);
        BindIntent pin = ReadBindIntent(reference);
        RevisionImage image = await authority.ReadBindingAsync(intent.Request, token);
        LocalVoterCredentialReplay.RequireAtLeast(Head(image), pin.Head);
        DateTimeOffset now = time.GetUtcNow();
        AcceptedVoterContinuation accepted = RequireRegistration(image, intent, pin.Registration, allowHandoff: true);
        RevisionCommand bind = accepted.Bind ?? throw new InvalidDataException("VoterContinuationLocalExactBindRequired");
        DateTimeOffset boundAt = bind.VoterContinuation!.Acceptance!.AtUtc;
        // Reconcile the committed transition at its accepted time. Local recovery does not renew the process lease.
        OnlineProcessLease original = VoterContinuationReducer.Lease(accepted.Registration!, pin.Registration.Index);
        OnboardingContractImage contracts = image.OnboardingContracts!;
        RevisionImage atBind = image with { OnboardingContracts = contracts with
        {
            OnlineProcesses = contracts.OnlineProcesses!.SetItem(original.NodeId, original)
        } };
        string? refusal = VoterContinuationBinding.Refusal(atBind, bind, boundAt, image.Index, replay: true);
        if (refusal is not null || boundAt > now || pin.MaterialDigest != Digest(intent))
        {
            throw new InvalidDataException("VoterContinuationLocalExactBindRequired");
        }

        RequireArtifact(image, intent, pin.Registration, pin.Artifact, now);
        VoterCredentialRequest request = intent.Request;
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        OperationalCredential credential = bind.VoterContinuation.Credential!;
        CommandResult decision = RequireDecision(image, bind);
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(pin.Artifact.CertificateDer);
        if (pin.Head.Index < pin.Registration.Index || pin.Head.Term < pin.Registration.Term
            || pin.Head.Index >= decision.Index || credential.Fingerprint != pin.Artifact.CertificateDigest
            || !VoterCredentialLeafTrust.ValidateDedicated(image, credential, certificate, now, server: false)
            || response is not null && response != new BindReceipt(request.OperationId, leaf.IssueId, decision, decision.Index))
        {
            throw new InvalidDataException("VoterContinuationLocalBindReceiptChanged");
        }

        if (ReadIntent(reference) != intent || Digest(ReadBindIntent(reference)) != Digest(pin))
        {
            throw new InvalidDataException("VoterContinuationLocalBindIntentChanged");
        }

        RequireCurrent(image, token);
        DateTimeOffset overlap = image.Operations.Credentials[request.Renewal.PreviousCredentialId].ExpiresAtUtc;
        return (new(intent, null, pin.Artifact), pin.Registration, new(Digest(pin), decision, boundAt, overlap), image);
    }

    private static AcceptedVoterContinuation RequireRegistration(RevisionImage image, LocalVoterCredentialIntent intent,
        CommandResult expected, bool allowHandoff = false)
    {
        VoterContinuationSnapshot.Validate(image);
        AcceptedVoterContinuation? accepted = image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(intent.Request.OperationId);
        if (accepted?.Registration is not { } registration || accepted.Request.VoterContinuation!.Context.Request != intent.Request
            || RequireDecision(image, registration) != expected
            || registration.VoterContinuation!.Context.Process.BootId != intent.Process!.BootId
            || registration.VoterContinuation.Context.Process.BootChallenge != intent.Process.BootChallenge)
        {
            throw new InvalidDataException("VoterContinuationLocalRegistrationChanged");
        }

        OnlineProcessLease original = VoterContinuationReducer.Lease(registration, expected.Index);
        OnlineProcessLease? current = image.OnboardingContracts!.OnlineProcesses?.GetValueOrDefault(registration.EntityId);
        if (current != original && (!allowHandoff || accepted.Bind is not { } bind
            || !LocalVoterContinuationQuarantine.HasAcceptedHandoff(image, bind, original, current)))
        {
            throw new InvalidDataException("VoterContinuationLocalRegistrationChanged");
        }

        return accepted;
    }

    private static CommandResult RequireDecision(RevisionImage image, RevisionCommand command)
    {
        CommandResult? result = RevisionDispatcher.ReadVoterCredentialDecision(image, command);
        if (result is not { Outcome: OperationOutcome.Committed, Reason: null, ConflictId: null, ReplacedBy: null, SubjectUserId: null }
            || result.ActorRevision != command.ActorRevision)
        {
            throw new InvalidDataException("VoterContinuationLocalExactDecisionRequired");
        }

        return result;
    }

    private static void RequireArtifact(RevisionImage image, LocalVoterCredentialIntent intent, CommandResult registration,
        EnrollmentIssuedArtifact artifact, DateTimeOffset now)
    {
        VoterCredentialArtifactValidation.RequireIssued(image, intent.Request, artifact,
            VoterContinuationAuthority.IssuanceDigest(intent.Request, registration.Digest), registration.Index, now);
    }

    private LocalVoterCredentialIntent ReadIntent(string reference)
    {
        var (intent, key) = OpenIntent(reference);
        using (key)
        {
            return intent;
        }
    }

    internal (LocalVoterCredentialIntent Intent, ECDsa Key) OpenIntent(string reference)
    {
        byte[] bundle = files.Read(reference, "intent") ?? throw new InvalidDataException("VoterContinuationLocalIntentMissing");
        try
        {
            LocalVoterCredentialIntent intent = LocalVoterCredentialMaterial.Decode(bundle, lease.InstallIdentity, reference,
                continuation: true, out ECDsa key);
            try
            {
                using ECDsa previous = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);
                return (intent, key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    private LocalVoterCredentialIntent Decode(string reference, byte[] bundle)
    {
        LocalVoterCredentialIntent intent = LocalVoterCredentialMaterial.Decode(bundle, lease.InstallIdentity, reference,
            continuation: true, out ECDsa key);
        using (key)
        {
            using ECDsa previous = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);
            return intent;
        }
    }

    private BindIntent ReadBindIntent(string reference)
    {
        return Read<BindIntent>(files, reference, LocalVoterCredentialFiles.CONTINUATION_BIND_INTENT)
            ?? throw new InvalidDataException("VoterContinuationLocalBindIntentMissing");
    }

    private void RequireCurrent(RevisionImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!authority.IsCurrent(image))
        {
            throw new InvalidDataException("VoterContinuationLocalAcceptedCutChanged");
        }
    }

    private static ProjectionHead Head(RevisionImage image) =>
        new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);

    public sealed record BindReceipt(Guid OperationId, Guid IssueId, CommandResult Decision, long CredentialRevision);
    internal sealed record BindIntent(string MaterialDigest, CommandResult Registration, EnrollmentIssuedArtifact Artifact, ProjectionHead Head);
    internal sealed record BindResult(LocalVoterCredentialBinding Binding, ProjectionHead Head);
}
