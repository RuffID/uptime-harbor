using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Ведёт текущий boot от защищённого replay до первой карантинной публикации.</summary>
public partial class ManagedVoterContinuationStartup(InstallationLease lease, IOptions<InstallationOptions> options,
    LocalSecretStore secrets, LocalVoterCredentialFiles files, LocalVoterContinuationInstallation local,
    ManagedVoterContinuationInstallation installation, VoterContinuationClient client, EnrollmentEndpoints endpoints,
    TimeProvider time)
{
    public static bool RequiresContinuation(RevisionImage image, LocalInstallationProfile profile, DateTimeOffset now)
    {
        if (profile is not { Mode: "Bootstrap", ManagedRestart: true, Phase: InstallationPhase.ClusterQuarantine,
            NodeId: Guid node, CredentialId: Guid baseline })
        {
            return false;
        }

        var latest = VoterCredentialPolicy.Latest(image, node);
        Guid previous = latest?.Renewal.Issuance.IssueId ?? baseline;
        return latest?.ContinuationRequestId is not null
            || image.Operations.Credentials.GetValueOrDefault(previous) is { } leaf && now >= leaf.ExpiresAtUtc;
    }

    /// <summary>Вызывающий удерживает InstallationState.Transition и подтвердил native membership и WAL.</summary>
    public async Task ContinueLockedAsync(LocalInstallationProfile profile, ManagedContinuationReplay replay,
        Action<Guid> reportAcceptedRequest, CancellationToken token)
    {
        LocalVoterCredentialIntent intent = OpenOrCreateLocked(profile, replay, token);
        LocalVoterCredentialIntent.StartupProcess startup = intent.Process!.Startup!;
        if (intent.Process.BootId != lease.BootId || intent.Process.BootChallenge != lease.BootChallenge)
        {
            await RecoverBoundLockedAsync(profile, replay, intent, reportAcceptedRequest, token);
            return;
        }

        VoterContinuationClient.Intent call = new(intent.Request, intent.CsrDer,
            new(lease.BootId, lease.BootChallenge, startup.Epoch, startup.RecoveryVersion),
            startup.MembershipVersion, startup.AudiencePeerAddress);
        using ECDsa previousKey = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);
        ManagedContinuationCut cut = await ReadAsync(token);
        if (cut.Slice.Requested is null)
        {
            cut.RequireCurrent(token);
            CommandResult response = await client.RequestAsync(call, previousKey, token);
            cut = await ReadAsync(token);
            RequireReceipt(response, cut.Slice.Requested);
        }

        CommandResult accepted = cut.Slice.Requested!.Decision;
        cut.RequireCurrent(token);
        files.WriteOnce(intent.KeyReference, LocalVoterCredentialFiles.CONTINUATION_REQUEST_RESULT, StrictOnboardingJson.Encode(accepted));
        cut.RequireCurrent(token);
        reportAcceptedRequest(accepted.OperationId);
        if (cut.Slice.Authorized is not { } approved)
        {
            return;
        }

        if (cut.Slice.Registered is null)
        {
            cut.RequireCurrent(token);
            CommandResult response = await client.RegisterAsync(call, previousKey, approved.Decision.Digest, token);
            cut = await ReadAsync(token);
            RequireReceipt(response, cut.Slice.Registered);
        }

        if (cut.Slice.Bound is null)
        {
            string registrationDigest = cut.Slice.Registered!.Decision.Digest;
            string? certificateDigest = installation.ReadPinnedBindLocked(intent, cut, token);
            if (certificateDigest is null)
            {
                cut.RequireCurrent(token);
                VoterContinuationWire.IssuedLeaf issued = await client.IssueAsync(call, previousKey, registrationDigest, token);
                cut = await ReadAsync(token);
                certificateDigest = installation.PinBindLocked(intent, cut, issued, token);
            }

            cut.RequireCurrent(token);
            VoterContinuationWire.BoundLeaf bound = await client.BindAsync(call, previousKey, registrationDigest, certificateDigest, token);
            cut = await ReadAsync(token);
            RequireReceipt(bound.Decision, cut.Slice.Bound);
        }

        await installation.PublishLockedAsync(profile, intent, cut, ReadAsync, token);

        async Task<ManagedContinuationCut> ReadAsync(CancellationToken cancellationToken)
        {
            long started = time.GetTimestamp();
            VoterContinuationWire.AcceptedSlice slice = await client.ReadAcceptedSliceAsync(replay, call, previousKey, cancellationToken);
            ManagedContinuationCut current = new(replay, slice, time, started);
            CommandResult? savedRequest = LocalVoterCredentialInstallationCoordinator.Read<CommandResult>(files,
                intent.KeyReference, LocalVoterCredentialFiles.CONTINUATION_REQUEST_RESULT);
            if (savedRequest is not null)
            {
                RequireReceipt(savedRequest, slice.Requested);
            }

            current.RequireCurrent(cancellationToken);
            return current;
        }
    }

    private LocalVoterCredentialIntent OpenOrCreateLocked(LocalInstallationProfile profile, ManagedContinuationReplay replay,
        CancellationToken token)
    {
        replay.RequireCurrent(token);
        RevisionImage image = replay.Image;
        DateTimeOffset now = time.GetUtcNow();
        OperationalNode node = image.Operations.Nodes[replay.NodeId];
        var latest = VoterCredentialPolicy.Latest(image, node.NodeId);
        if (!RequiresContinuation(image, profile, now) || profile.InstallIdentity != lease.InstallIdentity
            || replay.Process.BootId != lease.BootId || replay.Process.BootChallenge != lease.BootChallenge)
        {
            throw new InvalidDataException("VoterContinuationStartupProfileChanged");
        }

        Guid baseline = profile.CredentialId!.Value;
        Guid previousId = latest?.Renewal.Issuance.IssueId ?? baseline;
        RevisionImage previousTrust = image;
        if (latest?.ContinuationRequestId is { } continuation)
        {
            var bound = image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(continuation)?.Bind;
            if (bound is null || !VoterContinuationBinding.IsCurrentBinding(image, bound))
            {
                throw new InvalidDataException("VoterContinuationStartupReplayedBindChanged");
            }

            previousTrust = VoterContinuationBinding.BeforeBinding(image, latest.Renewal.Issuance.IssueId);
            previousId = latest.Renewal.PreviousCredentialId;
        }

        OperationalCredential previous = image.Operations.Credentials.GetValueOrDefault(previousId)
            ?? throw new InvalidDataException("VoterContinuationStartupPreviousLeafMissing");
        using X509Certificate2 oldLeaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(previous.PublicCertificate));

        if (previous.RevokedAtUtc.HasValue || previous.NodeId != node.NodeId
            || !VoterCredentialLeafTrust.RequiresDedicatedTrust(previousTrust, previous, oldLeaf)
            || !VoterCredentialLeafTrust.ValidatePrevious(previousTrust, previous, oldLeaf, previous.ExpiresAtUtc.AddSeconds(-1), legacy: null))
        {
            throw new UnauthorizedAccessException("VoterContinuationStartupPreviousLeafInvalid");
        }

        InstallationOptions configured = options.Value;
        string origin = endpoints.Origin(configured.CredentialContinuationOrigin + "/").AbsoluteUri;
        string address = configured.CredentialContinuationPeerAddress;
        OperationalNode? audience = image.Operations.Nodes.Values.SingleOrDefault(candidate =>
            candidate.NodeId != node.NodeId && candidate is { Removed: false, Voting: true, Activated: true }
            && candidate.Address == address);
        if (audience is null)
        {
            throw new InvalidDataException("VoterContinuationStartupAudienceMissing");
        }

        string reference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
            node.NodeId, node.StorageGeneration, previous.Id, continuation: true);
        if (files.HasSlot(reference))
        {
            var (saved, key) = local.OpenIntent(reference);
            using (key)
            {
                if (saved.Process is not { Startup: { } startup } || startup.Origin != origin
                    || startup.AudiencePeerAddress != address || startup.MembershipVersion != image.Operations.MembershipVersion
                    || saved.Request.BoundSource is not null
                    || latest?.ContinuationRequestId is not null && latest.Renewal != saved.Request.Renewal
                    || latest?.ContinuationRequestId is { } requestId && requestId != saved.Request.OperationId)
                {
                    throw new InvalidDataException("VoterContinuationStartupIntentChanged");
                }
            }

            return saved;
        }

        if (latest?.ContinuationRequestId is not null)
        {
            throw new InvalidDataException("VoterContinuationStartupIntentMissing");
        }

        var issuers = image.OnboardingContracts!.Issuers!.Values.Where(candidate =>
            candidate.NodeId == audience.NodeId && candidate.StorageGeneration == audience.StorageGeneration
            && candidate.Purpose == EnrollmentIssuer.PEER_PURPOSE && candidate.RevokedAtUtc is null
            && candidate.RetiredAtUtc is null && candidate.NotBeforeUtc <= now && now < candidate.NotAfterUtc).Take(2).ToArray();
        if (issuers.Length != 1)
        {
            throw new InvalidDataException("VoterContinuationStartupIssuerUnavailable");
        }

        OnlineProcessLease? predecessor = image.OnboardingContracts.OnlineProcesses?.GetValueOrDefault(node.NodeId);
        long epoch = 1;
        long recoveryVersion = replay.RecoveryVersion;
        if (predecessor is not null)
        {
            if (predecessor.BootId == lease.BootId && predecessor.BootChallenge == lease.BootChallenge)
            {
                epoch = predecessor.ProcessEpoch;
                recoveryVersion = predecessor.RecoveryVersion;
            }
            else
            {
                epoch = checked(predecessor.ProcessEpoch + 1);
            }
        }

        VoterContinuationProcess processCandidate = new(lease.BootId, lease.BootChallenge, epoch, recoveryVersion);
        if (!VoterContinuationReducer.ProcessCandidate(predecessor, processCandidate, now))
        {
            throw new UnauthorizedAccessException("VoterContinuationStartupProcessUnavailable");
        }

        AcceptedEnrollmentIssuer issuer = issuers[0];
        using ECDsa newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] csr = new CertificateRequest("CN=UptimeHarbor voter continuation", newKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x3F) + 1);
        DateTimeOffset start = new(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        DateTimeOffset notBefore = start.AddMinutes(-2);
        DateTimeOffset notAfter = start.AddDays(30);
        if (notBefore < issuer.NotBeforeUtc)
        {
            notBefore = issuer.NotBeforeUtc;
        }

        if (notAfter > issuer.NotAfterUtc)
        {
            notAfter = issuer.NotAfterUtc;
        }

        EnrollmentIssuance issuance = new(1, Guid.NewGuid(), issuer.Epoch, Convert.ToHexString(serial), node.NodeId,
            node.StorageGeneration, node.Address, EnrollmentCertificates.Hash(newKey.ExportSubjectPublicKeyInfo()),
            EnrollmentCertificates.Hash(csr), notBefore, notAfter);
        VoterCredentialRenewal renewal = new(previous.Id, previous.Fingerprint,
            EnrollmentCertificates.Hash(oldLeaf.PublicKey.ExportSubjectPublicKeyInfo()), latest?.CredentialRevision ?? 0, issuance);
        VoterCredentialRequest request = new(1, image.ClusterId, image.Generation, Guid.NewGuid(), renewal);
        Guid previousOwner;
        string previousReference;
        if (latest is null)
        {
            previousOwner = profile.AttemptId ?? throw new InvalidDataException("VoterContinuationStartupPreviousKeyMissing");
            previousReference = profile.KeyReference ?? throw new InvalidDataException("VoterContinuationStartupPreviousKeyMissing");
        }
        else
        {
            previousOwner = previous.Id;
            previousReference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
                node.NodeId, node.StorageGeneration, latest.Renewal.PreviousCredentialId);
        }

        LocalVoterCredentialIntent intent = new(1, lease.InstallIdentity, reference, request, Convert.ToBase64String(csr),
            previousOwner, previousReference)
        {
            Process = new(lease.BootId, lease.BootChallenge)
            {
                Startup = new(processCandidate.Epoch, processCandidate.RecoveryVersion, image.Operations.MembershipVersion, address, origin)
            }
        };

        using ECDsa previousKey = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);

        byte[] bundle = LocalVoterCredentialMaterial.Encode(intent, newKey);
        try
        {
            replay.RequireCurrent(token);
            files.WriteOnce(reference, "intent", bundle);
            var (saved, key) = local.OpenIntent(reference);
            using (key)
            {
                if (saved != intent)
                {
                    throw new InvalidDataException("VoterContinuationStartupIntentChanged");
                }
            }

            return saved;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    private static void RequireReceipt(CommandResult response, VoterContinuationWire.Stage? stage)
    {
        if (stage?.Decision != response)
        {
            throw new InvalidDataException("VoterContinuationStartupReceiptChanged");
        }
    }
}
