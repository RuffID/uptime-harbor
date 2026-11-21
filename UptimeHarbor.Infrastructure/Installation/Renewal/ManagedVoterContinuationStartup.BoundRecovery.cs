using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using static UptimeHarbor.Infrastructure.Installation.Renewal.LocalVoterCredentialInstallationCoordinator;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

public partial class ManagedVoterContinuationStartup
{
    private async Task RecoverBoundLockedAsync(LocalInstallationProfile profile, ManagedContinuationReplay replay,
        LocalVoterCredentialIntent original, Action<Guid> reportAcceptedRequest, CancellationToken token)
    {
        string reference = original.KeyReference;
        var pin = installation.RequireBindPin(original);
        var originalProcess = original.Process!;
        var setup = originalProcess.Startup!;
        VoterContinuationClient.Intent discovery = new(original.Request, original.CsrDer,
            new(originalProcess.BootId, originalProcess.BootChallenge, setup.Epoch, setup.RecoveryVersion),
            setup.MembershipVersion, setup.AudiencePeerAddress);
        using ECDsa previousKey = LocalVoterCredentialMaterial.OpenPrevious(original, secrets, files);
        ManagedContinuationCut discovered = await DiscoverAsync(token);
        OnlineProcessLease current = discovered.Slice.Process!;
        string intentStage = LocalVoterCredentialFiles.ContinuationRecoveryStage(lease.BootId, "intent");
        BoundRecoveryIntent? saved = Read<BoundRecoveryIntent>(files, reference, intentStage);
        var receipts = ReadReceipts();
        if (saved is null && receipts.Request is not null)
        {
            throw new InvalidDataException("VoterContinuationRecoveryIntentMissing");
        }

        bool currentBoot = current.BootId == lease.BootId && current.BootChallenge == lease.BootChallenge;
        if (currentBoot && saved is null || currentBoot && time.GetUtcNow() >= current.LeaseUntilUtc
            || !currentBoot && (time.GetUtcNow() < current.LeaseUntilUtc
                || current.BootId == lease.BootId || current.BootChallenge == lease.BootChallenge))
        {
            throw new UnauthorizedAccessException("VoterContinuationRecoveryProcessUnavailable");
        }

        var (binding, wrote) = installation.PinAcceptedBindLocked(original, discovered, pin, token);
        if (wrote)
        {
            discovered = await DiscoverAsync(token);
            if (discovered.Slice.Process != current)
            {
                throw new InvalidDataException("VoterContinuationRecoveryPredecessorChanged");
            }
        }

        VoterContinuationBoundSource source = new(original.Request.OperationId, binding.Binding.Result.Digest,
            binding.Binding.CredentialRevision);
        if (saved is null)
        {
            var request = original.Request with { OperationId = Guid.NewGuid(), BoundSource = source };
            VoterContinuationProcess candidate = new(lease.BootId, lease.BootChallenge,
                checked(current.ProcessEpoch + 1), discovered.Slice.RecoveryVersion);
            saved = new(request, candidate, current, discovered.Slice.Head);
            discovered.RequireCurrent(token);
            files.WriteOnce(reference, intentStage, StrictOnboardingJson.Encode(saved));
        }

        if (Read<BoundRecoveryIntent>(files, reference, intentStage) != saved
            || saved.Request != original.Request with { OperationId = saved.Request.OperationId, BoundSource = source }
            || saved.Process.BootId != lease.BootId || saved.Process.BootChallenge != lease.BootChallenge
            || saved.Process.RecoveryVersion != discovered.Slice.RecoveryVersion
            || !VoterContinuationReducer.ProcessCandidate(current, saved.Process, time.GetUtcNow())
            || !currentBoot && saved.Predecessor != current
            || currentBoot && (current.GrantId != VoterContinuationCommands.OperationId(VoterContinuationCommands.REGISTER,
                    saved.Request.OperationId)
                || discovered.Slice.ProcessRegistration?.Command.VoterContinuation?.Context.Predecessor != saved.Predecessor))
        {
            throw new InvalidDataException("VoterContinuationRecoveryIntentChanged");
        }

        VoterContinuationClient.Intent call = new(saved.Request, original.CsrDer, saved.Process,
            setup.MembershipVersion, setup.AudiencePeerAddress);
        ManagedContinuationCut cut = await ReadAsync(token);
        if (cut.Slice.Requested is null)
        {
            cut.RequireCurrent(token);
            CommandResult response = await client.RequestAsync(call, previousKey, token);
            cut = await ReadAsync(token);
            RequireReceipt(response, cut.Slice.Requested);
        }

        cut = await PinStageAsync(cut, "request", cut.Slice.Requested!, token);
        reportAcceptedRequest(cut.Slice.Requested!.Decision.OperationId);
        if (cut.Slice.Authorized is not { } approved)
        {
            return;
        }

        cut = await PinStageAsync(cut, "authorize", approved, token);
        if (cut.Slice.Registered is null)
        {
            cut.RequireCurrent(token);
            CommandResult response = await client.RegisterAsync(call, previousKey, approved.Decision.Digest, token);
            cut = await ReadAsync(token);
            RequireReceipt(response, cut.Slice.Registered);
        }

        cut = await PinStageAsync(cut, "register", cut.Slice.Registered!, token);
        await installation.PublishLockedAsync(profile, original, cut, ReadAsync, token);

        async Task<ManagedContinuationCut> DiscoverAsync(CancellationToken cancellationToken)
        {
            long started = time.GetTimestamp();
            AcceptedSlice slice = await client.DiscoverBoundAsync(replay, discovery, previousKey,
                new(pin.Registration.Digest, pin.Artifact.CertificateDigest), cancellationToken);
            var receipt = Read<CommandResult>(files, reference, LocalVoterCredentialFiles.CONTINUATION_REQUEST_RESULT)
                ?? throw new InvalidDataException("VoterContinuationOriginalRequestReceiptMissing");
            RequireReceipt(receipt, slice.History!.Requested);
            ManagedContinuationCut result = new(replay, slice, time, started);
            result.RequireCurrent(cancellationToken);
            return result;
        }

        async Task<ManagedContinuationCut> ReadAsync(CancellationToken cancellationToken)
        {
            long started = time.GetTimestamp();
            AcceptedSlice slice = await client.ReadAcceptedSliceAsync(replay, call, previousKey, cancellationToken);
            LocalVoterCredentialReplay.RequireAtLeast(slice.Head, saved.Head);
            if (Read<BoundRecoveryIntent>(files, reference, intentStage) != saved
                || slice.Requested is { } requested && requested.Command.VoterContinuation!.Context.Predecessor != saved.Predecessor)
            {
                throw new InvalidDataException("VoterContinuationRecoveryIntentChanged");
            }

            var accepted = ReadReceipts();
            VerifyReceipt(accepted.Request, slice.Requested);
            VerifyReceipt(accepted.Authorization, slice.Authorized);
            VerifyReceipt(accepted.Registration, slice.Registered);
            ManagedContinuationCut result = new(replay, slice, time, started);
            result.RequireCurrent(cancellationToken);
            return result;

            void VerifyReceipt(RecoveryReceipt? receipt, Stage? stage)
            {
                if (receipt is not null)
                {
                    if (receipt.Stage != stage)
                    {
                        throw new InvalidDataException("VoterContinuationStartupReceiptChanged");
                    }

                    LocalVoterCredentialReplay.RequireAtLeast(receipt.Head, saved.Head);
                    LocalVoterCredentialReplay.RequireAtLeast(slice.Head, receipt.Head);
                }
            }
        }

        (RecoveryReceipt? Request, RecoveryReceipt? Authorization, RecoveryReceipt? Registration) ReadReceipts()
        {
            RecoveryReceipt? requested = ReadReceipt("request");
            RecoveryReceipt? authorized = ReadReceipt("authorize");
            RecoveryReceipt? registered = ReadReceipt("register");
            if (requested is null && authorized is not null || authorized is null && registered is not null)
            {
                throw new InvalidDataException("VoterContinuationRecoveryReceiptPrefixMissing");
            }

            return (requested, authorized, registered);
        }

        RecoveryReceipt? ReadReceipt(string kind)
        {
            return Read<RecoveryReceipt>(files, reference, LocalVoterCredentialFiles.ContinuationRecoveryStage(lease.BootId, kind));
        }

        async Task<ManagedContinuationCut> PinStageAsync(ManagedContinuationCut verified, string kind, Stage stage,
            CancellationToken cancellationToken)
        {
            string name = LocalVoterCredentialFiles.ContinuationRecoveryStage(lease.BootId, kind);
            if (ReadReceipt(kind) is not null)
            {
                return verified;
            }

            verified.RequireCurrent(cancellationToken);
            files.WriteOnce(reference, name, StrictOnboardingJson.Encode(new RecoveryReceipt(stage, verified.Slice.Head)));
            return await ReadAsync(cancellationToken);
        }
    }

    private sealed record BoundRecoveryIntent(VoterCredentialRequest Request, VoterContinuationProcess Process,
        OnlineProcessLease Predecessor, ProjectionHead Head);

    private sealed record RecoveryReceipt(Stage Stage, ProjectionHead Head);
}
