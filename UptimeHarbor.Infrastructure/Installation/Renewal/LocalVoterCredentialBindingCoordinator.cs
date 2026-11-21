using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Durable Bind intent/result на прежних lock/files/transport. Не устанавливает ключ и не открывает gates.</summary>
public class LocalVoterCredentialBindingCoordinator(LocalVoterCredentialPreparation preparation,
    LocalVoterCredentialFiles files, InstallationState installation, VoterCredentialRenewalAuthority authority,
    IRevisionTransport transport, TimeProvider time, ILogger<LocalVoterCredentialBindingCoordinator> logger)
{
    public async Task<LocalVoterCredentialBinding> BindAsync(string reference,
        VoterCredentialSigningRequest call, CancellationToken token)
    {
        return await BindCoreAsync(reference, call, null, token);
    }

    internal Task<LocalVoterCredentialBinding> BindAsync(string reference, VoterCredentialSigningRequest call,
        IVoterCredentialAdmissionClient? admission, CancellationToken token)
    {
        return BindCoreAsync(reference, call, admission, token);
    }

    internal Task<LocalVoterCredentialBinding> RecoverAcceptedAsync(string reference, CancellationToken token)
    {
        return BindCoreAsync(reference, null, null, token);
    }

    private async Task<LocalVoterCredentialBinding> BindCoreAsync(string reference,
        VoterCredentialSigningRequest? call, IVoterCredentialAdmissionClient? admission, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialState local = ReadMaterial(reference);
            if (call is not null && (call.ProofRequest.Request != local.Intent.Request || call.ProofRequest.CsrDer != local.Intent.CsrDer))
            {
                throw new InvalidDataException("VoterCredentialLocalRequestChanged");
            }

            LocalVoterCredentialBindIntent? intent = ReadIntent(reference, local);
            LocalVoterCredentialBinding? saved = ReadResult(reference);
            if ((intent is null || saved is null) && files.HasInstallationRecords(reference))
            {
                throw new InvalidDataException("VoterCredentialLocalInstallationBindMissing");
            }

            RevisionImage image = await authority.ReadBindingAsync(local.Intent.Request, token);
            VoterCredentialRotation rotation = LocalVoterCredentialBindingPolicy.RequireProjection(local, image, time.GetUtcNow());
            if (call is null && rotation.CredentialRevision == 0)
            {
                throw new UnauthorizedAccessException("VoterCredentialLocalExactBindRequired");
            }

            if (intent is null)
            {
                if (saved is not null || rotation.CredentialRevision != 0)
                {
                    throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
                }

                image = await ConfirmPreBindAsync(local, call!, token);
                intent = LocalVoterCredentialBindingPolicy.Create(local, Guid.NewGuid());
                files.WriteOnce(reference, "bind-intent", StrictOnboardingJson.Encode(intent));
                intent = ReadIntent(reference, local) ?? throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
                logger.LogInformation("VoterCredentialLocalBindIntentSaved {OperationId} {IssueId}",
                    intent.Command.OperationId, local.Intent.Request.Renewal.Issuance.IssueId);
            }

            IReadOnlyList<CommandResult>? response = null;
            if (rotation.CredentialRevision == 0)
            {
                if (saved is not null || image.Results.ContainsKey(intent.Command.OperationId))
                {
                    throw new InvalidDataException("VoterCredentialLocalBindResultConflicts");
                }

                // Перед отправкой сверяем исходную команду, PoP/CSR и DER с одним свежим accepted срезом.
                image = await authority.ConfirmBindingAsync(intent.Command, call!, local.Artifact!, token);
                LocalVoterCredentialBindingPolicy.RequireProjection(local, image, time.GetUtcNow());
                if (image.Results.ContainsKey(intent.Command.OperationId))
                {
                    throw new InvalidDataException("VoterCredentialLocalBindResultConflicts");
                }

                local = ReadMaterial(reference);
                if (ReadIntent(reference, local) != intent)
                {
                    throw new InvalidDataException("VoterCredentialLocalBindIntentChanged");
                }

                RuntimeGenerationContext.Verify();
                if (admission is not null)
                {
                    response = [await admission.BindAsync(preparation.ProveBind(local, call!, intent.Command), token)];
                }
                else
                {
                    response = await transport.SendAsync([intent.Command], token);
                }
            }

            var (_, accepted, _) = await ReconcileAsync(reference, intent, token);
            if (response is not null && (response.Count != 1 || response[0] != accepted.Result))
            {
                throw new InvalidDataException("VoterCredentialLocalBindResponseChanged");
            }

            if (saved is not null && saved != accepted)
            {
                throw new InvalidDataException("VoterCredentialLocalBindResultChanged");
            }

            files.WriteOnce(reference, "bind-result", StrictOnboardingJson.Encode(accepted));
            var (_, confirmed, _) = await ReconcileAsync(reference, intent, token);
            if (ReadResult(reference) != confirmed)
            {
                throw new InvalidDataException("VoterCredentialLocalBindResultChanged");
            }

            if (saved is null)
            {
                logger.LogInformation("VoterCredentialLocalBindRecorded {OperationId} {CredentialRevision}",
                    confirmed.Result.OperationId, confirmed.CredentialRevision);
            }

            return confirmed;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task<LocalVoterCredentialBinding> ReadBoundAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            var (_, current, _) = await ReadBoundCoreAsync(reference, token);
            return current;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    // Координатор установки вызывает этот метод под тем же Transition.
    internal async Task<(LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image)>
        ReadBoundCoreAsync(string reference, CancellationToken token)
    {
        LocalVoterCredentialState local = ReadMaterial(reference);
        LocalVoterCredentialBindIntent intent = ReadIntent(reference, local)
            ?? throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
        LocalVoterCredentialBinding saved = ReadResult(reference)
            ?? throw new InvalidDataException("VoterCredentialLocalBindResultMissing");
        var current = await ReconcileAsync(reference, intent, token);
        if (saved != current.Binding || ReadResult(reference) != saved)
        {
            throw new InvalidDataException("VoterCredentialLocalBindResultChanged");
        }

        return current;
    }

    // Native replay establishes the local cut. Its caller may use the slot only for quarantine.
    internal (LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image)
        ReadReplayed(string reference, RevisionImage image)
    {
        LocalVoterCredentialState local = ReadMaterial(reference);
        LocalVoterCredentialBindIntent intent = ReadIntent(reference, local)
            ?? throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
        LocalVoterCredentialBinding bound = LocalVoterCredentialBindingPolicy.RequireBound(local, intent, image, time.GetUtcNow());
        RequireReplayResult(reference, bound);

        return (local, bound, image);
    }

    internal LocalVoterPendingBindReplay? ReadPendingReplay(string reference, RevisionImage image,
        Guid previousCredential, long previousRevision)
    {
        if (files.HasInstallationRecords(reference) || ReadResult(reference) is not null)
        {
            throw new InvalidDataException("VoterCredentialInstallationAheadOfReplay");
        }

        if (files.Read(reference, "bind-intent") is null)
        {
            return null;
        }

        LocalVoterCredentialState local = ReadMaterial(reference);
        var renewal = local.Intent.Request.Renewal;
        if (renewal.PreviousCredentialId != previousCredential || renewal.PreviousCredentialRevision != previousRevision)
        {
            throw new InvalidDataException("VoterCredentialLocalPreviousRegistryChanged");
        }

        LocalVoterCredentialBindIntent intent = ReadIntent(reference, local)
            ?? throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
        VoterCredentialRotation rotation = LocalVoterCredentialBindingPolicy.RequireProjection(local, image, time.GetUtcNow());
        RequireUnbound(local.Intent.Request, intent, rotation, image);
        return new(local.Intent, intent, image.Results[local.Intent.Request.OperationId], Head(image));
    }

    internal async Task<(RevisionImage Image, LocalVoterCredentialBinding? Binding)> ConfirmPendingReplayAsync(
        LocalVoterPendingBindReplay replay, CancellationToken token)
    {
        string reference = replay.Material.KeyReference;
        RevisionImage image = await authority.ReadBindingAsync(replay.Material.Request, token);
        LocalVoterCredentialReplay.RequireAtLeast(Head(image), replay.Head);

        LocalVoterCredentialState local = ReadMaterial(reference);
        if (local.Intent != replay.Material || ReadIntent(reference, local) != replay.Bind
            || image.Results.GetValueOrDefault(local.Intent.Request.OperationId) != replay.Authorization)
        {
            throw new InvalidDataException("VoterCredentialPendingBindChanged");
        }

        VoterCredentialRotation rotation = LocalVoterCredentialBindingPolicy.RequireProjection(local, image, time.GetUtcNow());
        if (rotation.CredentialRevision == 0)
        {
            if (ReadResult(reference) is not null || files.HasInstallationRecords(reference))
            {
                throw new InvalidDataException("VoterCredentialInstallationAheadOfReplay");
            }

            RequireUnbound(local.Intent.Request, replay.Bind, rotation, image);
            return (image, null);
        }

        return (image, LocalVoterCredentialBindingPolicy.RequireBound(local, replay.Bind, image, time.GetUtcNow()));
    }

    private void RequireUnbound(VoterCredentialRequest request, LocalVoterCredentialBindIntent intent,
        VoterCredentialRotation rotation, RevisionImage image)
    {
        if (rotation.CredentialRevision != 0 || image.Results.ContainsKey(intent.Command.OperationId)
            || image.Operations.Credentials.ContainsKey(request.Renewal.Issuance.IssueId))
        {
            throw new InvalidDataException("VoterCredentialLocalBindResultConflicts");
        }

        VoterCredentialAuthorityPolicy.RequireCurrent(image, request, time.GetUtcNow());
    }

    private static ProjectionHead Head(RevisionImage image)
    {
        return new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);
    }

    // Called under Transition. Reconciliation may persist the result of an existing Bind only.
    internal async Task<(LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image)>
        RecoverRestartResultAsync(string reference, LocalVoterCredentialReplay replay, CancellationToken token)
    {
        LocalVoterCredentialState local = ReadMaterial(reference);
        LocalVoterCredentialBindIntent intent = ReadIntent(reference, local)
            ?? throw new InvalidDataException("VoterCredentialLocalBindIntentMissing");
        var current = await ConfirmRestartBindAsync(reference, intent, replay, token);
        if (RequireReplayResult(reference, current.Binding))
        {
            return current;
        }

        RevisionImage firstCut = current.Image;
        var writtenAt = replay with { Head = new(firstCut.ClusterId, firstCut.Generation, firstCut.Index, firstCut.Term, firstCut.Digest) };
        files.WriteOnce(reference, "bind-result", StrictOnboardingJson.Encode(current.Binding));
        current = await ConfirmRestartBindAsync(reference, intent, writtenAt, token);
        if (ReadResult(reference) != current.Binding)
        {
            throw new InvalidDataException("VoterCredentialLocalBindResultChanged");
        }

        logger.LogInformation("VoterCredentialLocalBindRecorded {OperationId} {CredentialRevision}",
            current.Binding.Result.OperationId, current.Binding.CredentialRevision);
        return current;
    }

    private async Task<(LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image)>
        ConfirmRestartBindAsync(string reference, LocalVoterCredentialBindIntent intent, LocalVoterCredentialReplay replay,
            CancellationToken token)
    {
        var current = await ReconcileAsync(reference, intent, token);
        replay.RequirePendingEvidence(current.Local, current.Image);
        if (current.Binding != replay.Binding)
        {
            throw new InvalidDataException("VoterCredentialReplayedBindChanged");
        }

        RevisionImage image = current.Image;
        LocalVoterCredentialReplay.RequireAtLeast(new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest), replay.Head);
        return current;
    }

    private bool RequireReplayResult(string reference, LocalVoterCredentialBinding bound)
    {
        LocalVoterCredentialBinding? saved = ReadResult(reference);
        if (saved is null)
        {
            if (files.HasInstallationRecords(reference))
            {
                throw new InvalidDataException("VoterCredentialLocalInstallationBindMissing");
            }

            return false;
        }

        if (saved != bound)
        {
            throw new InvalidDataException("VoterCredentialLocalBindResultChanged");
        }

        return true;
    }

    private async Task<RevisionImage> ConfirmPreBindAsync(LocalVoterCredentialState local,
        VoterCredentialSigningRequest call, CancellationToken token)
    {
        EnrollmentIssuance leaf = local.Intent.Request.Renewal.Issuance;
        RevisionImage image = await authority.ConfirmSigningAsync(call, leaf.NodeId, leaf.StorageGeneration, token);
        LocalVoterCredentialBindingPolicy.RequireProjection(local, image, time.GetUtcNow());
        return image;
    }

    private async Task<(LocalVoterCredentialState Local, LocalVoterCredentialBinding Binding, RevisionImage Image)>
        ReconcileAsync(string reference,
        LocalVoterCredentialBindIntent intent, CancellationToken token)
    {
        LocalVoterCredentialState local = ReadMaterial(reference);
        if (ReadIntent(reference, local) != intent)
        {
            throw new InvalidDataException("VoterCredentialLocalBindIntentChanged");
        }

        RevisionImage image = await authority.ReadBindingAsync(local.Intent.Request, token);
        // После await заново проверяется физический material, а не retained memory bundle.
        local = ReadMaterial(reference);
        if (ReadIntent(reference, local) != intent)
        {
            throw new InvalidDataException("VoterCredentialLocalBindIntentChanged");
        }

        LocalVoterCredentialBinding bound = LocalVoterCredentialBindingPolicy.RequireBound(local, intent, image, time.GetUtcNow());
        return (local, bound, image);
    }

    private LocalVoterCredentialState ReadMaterial(string reference)
    {
        LocalVoterCredentialState local = preparation.ReadCore(reference)
            ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
        if (local.Authorization is null || local.Artifact is null)
        {
            throw new InvalidDataException("VoterCredentialLocalBindPinsMissing");
        }

        return local;
    }

    private LocalVoterCredentialBindIntent? ReadIntent(string reference, LocalVoterCredentialState local)
    {
        byte[]? bytes = files.Read(reference, "bind-intent");
        if (bytes is null)
        {
            return null;
        }

        LocalVoterCredentialBindIntent intent = StrictOnboardingJson.Decode<LocalVoterCredentialBindIntent>(bytes);
        LocalVoterCredentialBindingPolicy.RequireIntent(local, intent);
        return intent;
    }

    private LocalVoterCredentialBinding? ReadResult(string reference)
    {
        byte[]? bytes = files.Read(reference, "bind-result");
        if (bytes is null)
        {
            return null;
        }

        return StrictOnboardingJson.Decode<LocalVoterCredentialBinding>(bytes);
    }
}
