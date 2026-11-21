using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Атомарный credential/trust snapshot; пустой provider закрывает handshake.</summary>
public class DynamicPeerTls(InstallationState state, RevisionStateStore? revisions = null, TimeProvider? time = null,
    EnrollmentProbeSession? probes = null)
{
    private Publication publication = new(null, null, 0, false);

    public X509Certificate2? Certificate
    {
        get
        {
            Publication current = Volatile.Read(ref publication);
            Snapshot? snapshot = Capture(current);
            return snapshot?.Certificate ?? (current.Floor is null && !current.Blocked ? probes?.Certificate : null);
        }
    }

    public bool Validate(X509Certificate2 certificate)
    {
        Publication current = Volatile.Read(ref publication);
        Snapshot? snapshot = Capture(current);
        if (snapshot is null)
        {
            return current.Floor is null && !current.Blocked && probes?.Validate(certificate) == true;
        }

        return Validate(snapshot, certificate);
    }

    public X509Certificate2? ProbeCertificate => ProbeAllowed() ? probes?.Certificate : null;

    public bool ValidateProbe(X509Certificate2 certificate)
    {
        return ProbeAllowed() && probes?.Validate(certificate) == true;
    }

    private bool ProbeAllowed()
    {
        Publication current = Volatile.Read(ref publication);
        return current.Current is null && current.Floor is null && !current.Blocked;
    }

    public bool Validate(Snapshot snapshot, X509Certificate2 certificate)
    {
        if (snapshot.BeforeNativeStart is not null || !IsUsable(snapshot))
        {
            return false;
        }

        RevisionImage? image = revisions?.Current;
        if (snapshot.DedicatedIssuerEpoch.HasValue && image?.OnboardingContracts?.GenesisInitialized == true)
        {
            return EnrollmentLiveTrust.Validate(image, certificate, (time ?? TimeProvider.System).GetUtcNow())
                && ReferenceEquals(image, revisions?.Current);
        }

        return ValidatePinned(snapshot, certificate);
    }

    private static bool ValidatePinned(Snapshot snapshot, X509Certificate2 certificate)
    {
        if (!snapshot.Pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)))
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(snapshot.Root);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
        if (snapshot.DedicatedIssuerEpoch.HasValue)
        {
            // Только dedicated application CA. Legacy Direct продолжает требовать offline CRL.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        }

        return snapshot.AcceptedValidator(certificate) && chain.Build(certificate);
    }

    public bool ValidateRequest(X509Certificate2 certificate, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now)
    {
        Snapshot? snapshot = Capture();
        return snapshot is not null && ValidateRequest(snapshot, certificate, options, credential, now);
    }

    public bool ValidateRequest(Snapshot snapshot, X509Certificate2 certificate, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now)
    {
        if (snapshot.BeforeNativeStart is not null || !IsUsable(snapshot))
        {
            return false;
        }

        RevisionImage? image = revisions?.Current;
        if (snapshot.DedicatedIssuerEpoch.HasValue && image?.OnboardingContracts?.GenesisInitialized == true)
        {
            OperationalCredential? accepted = image.Operations.Credentials.GetValueOrDefault(credential.CredentialId);
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
            return accepted is not null && node is not null && accepted.NodeId == node.NodeId
                && image.ClusterId == options.ClusterId && image.Generation == options.Generation
                && node.StorageGeneration == credential.StorageGeneration && node.Address == credential.Address.AbsoluteUri
                && accepted.Fingerprint == credential.Fingerprint && accepted.Fingerprint == EnrollmentCertificates.Hash(certificate.RawData)
                && now < credential.ExpiresAtUtc && EnrollmentLiveTrust.Validate(image, certificate, now)
                && ReferenceEquals(image, revisions?.Current);
        }

        if (!ValidatePinned(snapshot, certificate))
        {
            return false;
        }

        if (snapshot.DedicatedIssuerEpoch.HasValue)
        {
            string spki = EnrollmentCertificates.Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo());
            EnrollmentRequest request = new(1, options.ClusterId, options.Generation, credential.CredentialId,
                credential.CredentialId, credential.NodeId, credential.StorageGeneration, credential.Address.AbsoluteUri,
                spki, new string('0', 64));
            EnrollmentIssuance template = new(1, credential.CredentialId, snapshot.DedicatedIssuerEpoch.Value,
                certificate.SerialNumber, credential.NodeId, credential.StorageGeneration, credential.Address.AbsoluteUri,
                spki, request.CsrDigest, new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
            return now < credential.ExpiresAtUtc && EnrollmentCertificates.ValidateLeaf(certificate, snapshot.Root,
                request, template, credential.Fingerprint, now, false);
        }

        // Legacy HTTP повторяет fingerprint, EKU, SAN и expiry; PEM из старого config не нужен.
        return new PeerCertificateValidator().Validate(certificate, snapshot.Root, options, credential, now,
            offlineRevocation: true);
    }

    public void Install(long generation, X509Certificate2 certificate, X509Certificate2 root,
        IEnumerable<string> acceptedPins, Func<X509Certificate2, bool> acceptedValidator, Guid? dedicatedIssuerEpoch = null)
    {
        LocalInstallationProfile? profile = state.Current;
        if (profile is null || generation != profile.RuntimeGeneration || !certificate.HasPrivateKey
            || profile.TrustAnchorFingerprint != root.GetCertHashString(HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("StalePeerCredentialGeneration");
        }

        Publication before = Volatile.Read(ref publication);
        if (before.Blocked || before.Floor is not null)
        {
            throw new InvalidOperationException("ConfirmedPeerCredentialRequired");
        }

        Snapshot next = new(generation, profile.CredentialId, 0, before.Epoch, certificate, root,
            acceptedPins.ToHashSet(StringComparer.OrdinalIgnoreCase), acceptedValidator, dedicatedIssuerEpoch);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref publication, before with { Current = next }, before), before))
        {
            throw new InvalidOperationException("PeerCredentialPublicationChanged");
        }
    }

    public void Revoke()
    {
        UpdateFailure(block: false);
    }

    internal void BlockPublication()
    {
        UpdateFailure(block: true);
    }

    private void UpdateFailure(bool block)
    {
        Publication before;
        do
        {
            before = Volatile.Read(ref publication);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref publication,
            before with { Blocked = before.Blocked || block, Current = null, Epoch = checked(before.Epoch + 1) }, before), before));
    }

    private void BlockIfCurrent(Publication before)
    {
        Publication blocked = before with { Blocked = true, Current = null, Epoch = checked(before.Epoch + 1) };
        Interlocked.CompareExchange(ref publication, blocked, before);
    }

    internal void PublishContinuationBeforeNativeStart(LocalInstallationProfile profile, ManagedContinuationCut cut,
        LocalVoterContinuationQuarantine quarantine, LocalVoterCredentialInstallation installed,
        X509Certificate2 certificate, X509Certificate2 root)
    {
        var slice = cut.Slice;
        Publication before = Volatile.Read(ref publication);
        if (!ReferenceEquals(profile, state.Current) || state.CommonOpen || profile.Phase != InstallationPhase.ClusterQuarantine
            || profile.NodeId != cut.Replay.NodeId || profile.BootId != cut.Replay.Process.BootId
            || !ReferenceEquals(revisions?.Current, cut.Replay.Image) || revisions.RecoveryVersion != cut.Replay.RecoveryVersion
            || !cut.IsCurrent() || !certificate.HasPrivateKey || slice.CredentialBinding?.Command != quarantine.Bind
            || slice.Registered is null || slice.Process != quarantine.Original
            || quarantine.Original.BootId != cut.Replay.Process.BootId
            || quarantine.Original.BootChallenge != cut.Replay.Process.BootChallenge
            || installed.MaterialReference != quarantine.Reference
            || installed.IssueId != slice.CredentialBinding.Command.VoterContinuation!.Credential!.Id
            || installed.CredentialRevision != slice.CredentialBinding.Decision.Index
            || installed.CertificateFingerprint != EnrollmentCertificates.Hash(certificate.RawData)
            || slice.Issuer.CertificateDigest != EnrollmentCertificates.Hash(root.RawData)
            || installed.CredentialRevision < before.Revision
            || before.Floor is { } floor && (slice.Head.ClusterId != floor.ClusterId || slice.Head.Generation != floor.Generation
                || slice.Head.Index < floor.Index || slice.Head.Term < floor.Term
                || slice.Head.Index == floor.Index && slice.Head != floor
                || installed.CredentialRevision == before.Revision && installed.IssueId != before.CredentialId))
        {
            throw new InvalidDataException("VoterContinuationStartupPublicationChanged");
        }

        Snapshot next = new(profile.RuntimeGeneration, installed.IssueId, installed.CredentialRevision, before.Epoch,
            certificate, root, [], _ => false, slice.Issuer.Epoch, continuation: quarantine, beforeNativeStart: cut);
        Publication after = new(next, slice.Head, before.Epoch, false, installed.CredentialRevision, installed.IssueId,
            cut.Replay.RecoveryVersion);
        RuntimeGenerationContext.Verify();
        if (!cut.IsCurrent() || !ReferenceEquals(Interlocked.CompareExchange(ref publication, after, before), before))
        {
            throw new InvalidDataException("VoterContinuationStartupPublicationChanged");
        }
    }

    internal Snapshot PublishConfirmed(RevisionStateStore store, RevisionImage image, long recoveryVersion,
        LocalInstallationProfile profile, Guid credentialId, long revision, X509Certificate2 certificate,
        X509Certificate2 root, Guid issuerEpoch, LocalVoterCredentialReplay? replay = null,
        LocalVoterPendingBindReplay? pendingBind = null, bool pendingConfirmed = false,
        LocalVoterContinuationQuarantine? continuation = null)
    {
        Publication before = Volatile.Read(ref publication);
        ProjectionHead floor = new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);
        if (!ReferenceEquals(store, revisions) || profile != state.Current || !AllowsPeer(profile)
            || !certificate.HasPrivateKey || !AtLeast(image, before.Floor)
            || revision == 0 && (pendingBind is null || credentialId != profile.CredentialId)
            || revision < before.Revision
            || before.Floor is not null && revision == before.Revision && credentialId != before.CredentialId)
        {
            throw new InvalidOperationException("StalePeerCredentialPublication");
        }

        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        if (!EnrollmentLiveTrust.Validate(image, certificate, now))
        {
            throw new InvalidOperationException("PeerCredentialPublicationTrustUnavailable");
        }

        LocalVoterContinuationQuarantine? quarantine = continuation;
        if (continuation is not null && image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(continuation.Original.NodeId)
            is { CredentialContinuation: false })
        {
            quarantine = null;
        }

        Snapshot? next = before.Current;
        if (next is null || next.CredentialRevision != revision || next.Generation != profile.RuntimeGeneration
            || next.RestartReplay != replay || next.PendingBind != pendingBind || next.PendingConfirmed != pendingConfirmed
            || next.Continuation != quarantine)
        {
            next = new(profile.RuntimeGeneration, credentialId, revision, before.Epoch, certificate, root,
                [], _ => false, issuerEpoch, replay, pendingBind, pendingConfirmed, quarantine);
        }

        Publication after = new(next, floor, before.Epoch, false, revision, credentialId, recoveryVersion);
        bool swapped = false;
        RuntimeGenerationContext.Verify();
        if (!store.TryAtCurrent(image, recoveryVersion, () =>
            {
                if (profile != state.Current || quarantine is not null && state.CommonOpen
                    || continuation is not null && !continuation.Matches(image, (time ?? TimeProvider.System).GetUtcNow()))
                {
                    return;
                }

                swapped = ReferenceEquals(Interlocked.CompareExchange(ref publication, after, before), before);
            }) || !swapped)
        {
            throw new InvalidOperationException("PeerCredentialAcceptedCutChanged");
        }

        return next;
    }

    public Snapshot? Capture()
    {
        return Capture(Volatile.Read(ref publication));
    }

    public Snapshot? CaptureWorkingBootstrap(RevisionImage image, LocalInstallationProfile profile)
    {
        return CaptureBootstrap(image, profile, continuationRenewal: false);
    }

    public Snapshot? CaptureBootstrapProcessRenewal(RevisionImage image, LocalInstallationProfile profile)
    {
        return CaptureBootstrap(image, profile, continuationRenewal: true);
    }

    private Snapshot? CaptureBootstrap(RevisionImage image, LocalInstallationProfile profile, bool continuationRenewal)
    {
        if (profile != state.Current || profile.Mode != "Bootstrap" || profile.NodeId is not Guid node
            || !profile.ManagedRestart && profile.Phase != InstallationPhase.ClusterReady)
        {
            return null;
        }

        var latest = VoterCredentialPolicy.Latest(image, node);
        Snapshot? snapshot = Capture();
        if (snapshot is null || revisions?.Available != true || !ReferenceEquals(image, revisions.Current)
            || snapshot.CredentialId != (latest?.Renewal.Issuance.IssueId ?? profile.CredentialId)
            || snapshot.CredentialRevision != (latest?.CredentialRevision ?? 0))
        {
            return null;
        }

        if (!snapshot.Quarantined || continuationRenewal && snapshot.Continuation is not null)
        {
            return snapshot;
        }

        return null;
    }

    internal ECDsa OpenWorkingBootstrapKey(RevisionImage image, LocalInstallationProfile profile, OnlineGrant request,
        bool processRenewal = false)
    {
        Snapshot? snapshot = CaptureBootstrap(image, profile, continuationRenewal: processRenewal);
        if (snapshot is not { CredentialRevision: > 0 } || snapshot.CredentialId != request.CredentialId
            || snapshot.Fingerprint != request.LeafDigest || snapshot.IssuerEpoch != request.IssuerEpoch)
        {
            throw new UnauthorizedAccessException("ManagedRestartCredentialChanged");
        }

        return snapshot.Certificate.GetECDsaPrivateKey() ?? throw new InvalidDataException("ManagedRestartKeyMissing");
    }

    private Snapshot? Capture(Publication current)
    {
        Snapshot? snapshot = current.Current;
        return snapshot is not null && IsUsable(snapshot, current) ? snapshot : null;
    }

    public bool IsUsable(Snapshot snapshot)
    {
        return IsUsable(snapshot, Volatile.Read(ref publication));
    }

    private bool IsUsable(Snapshot snapshot, Publication current)
    {
        LocalInstallationProfile? profile = state.Current;
        if (current.Blocked || current.Current is null || profile is null || !AllowsPeer(profile)
            || snapshot.Epoch != current.Epoch || snapshot.Generation != profile.RuntimeGeneration)
        {
            return false;
        }

        if (current.Floor is null)
        {
            return ReferenceEquals(snapshot, current.Current);
        }

        if (snapshot.BeforeNativeStart is { } startup)
        {
            return ReferenceEquals(snapshot, current.Current) && !state.CommonOpen
                && startup.IsCurrent() && (time ?? TimeProvider.System).GetUtcNow() < startup.Slice.CredentialBinding!.Command
                    .VoterContinuation!.Credential!.ExpiresAtUtc;
        }

        RevisionImage? image = revisions?.Current;
        if (image is null || !AtLeast(image, current.Floor) || revisions?.Available != true
            || revisions.RecoveryVersion != current.RecoveryVersion)
        {
            BlockIfCurrent(current);
            return false;
        }

        if (snapshot.CredentialId is not Guid id)
        {
            return false;
        }

        if (snapshot.Quarantined && !ReferenceEquals(snapshot, current.Current))
        {
            return false;
        }

        if (snapshot.Continuation is { } continuation && !continuation.Matches(image, (time ?? TimeProvider.System).GetUtcNow()))
        {
            return false;
        }

        if (snapshot.RestartReplay is { } replay)
        {
            if (!replay.Matches(image, profile.NodeId!.Value, id))
            {
                BlockIfCurrent(current);
                return false;
            }
        }

        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(id);
        if (credential is null || credential.NodeId != profile.NodeId || credential.Fingerprint != snapshot.Fingerprint
            || !EnrollmentLiveTrust.Validate(image, snapshot.Certificate, (time ?? TimeProvider.System).GetUtcNow()))
        {
            return false;
        }

        Publication latest = Volatile.Read(ref publication);
        return ReferenceEquals(image, revisions.Current) && revisions.Available
            && revisions.RecoveryVersion == current.RecoveryVersion && !latest.Blocked && latest.Epoch == current.Epoch;
    }

    private static bool AllowsPeer(LocalInstallationProfile profile)
    {
        return profile.Phase is InstallationPhase.ClusterQuarantine or InstallationPhase.CatchingUp
            or InstallationPhase.AwaitingActivation or InstallationPhase.ClusterReady;
    }

    private static bool AtLeast(RevisionImage image, ProjectionHead? floor)
    {
        return floor is null || image.ClusterId == floor.ClusterId && image.Generation == floor.Generation
            && image.Index >= floor.Index && image.Term >= floor.Term
            && (image.Index != floor.Index || image.Term == floor.Term && image.Digest == floor.Digest);
    }

    private sealed record Publication(Snapshot? Current, ProjectionHead? Floor, long Epoch, bool Blocked,
        long Revision = 0, Guid CredentialId = default, long RecoveryVersion = 0);

    public sealed class Snapshot
    {
        internal Snapshot(long generation, Guid? credentialId, long credentialRevision, long epoch,
            X509Certificate2 certificate, X509Certificate2 root, HashSet<string> pins,
            Func<X509Certificate2, bool> acceptedValidator, Guid? dedicatedIssuerEpoch, LocalVoterCredentialReplay? replay = null,
            LocalVoterPendingBindReplay? pendingBind = null, bool pendingConfirmed = false,
            LocalVoterContinuationQuarantine? continuation = null, ManagedContinuationCut? beforeNativeStart = null)
        {
            Generation = generation;
            CredentialId = credentialId;
            CredentialRevision = credentialRevision;
            RestartReplay = replay;
            PendingBind = pendingBind;
            PendingConfirmed = pendingConfirmed;
            Continuation = continuation;
            BeforeNativeStart = beforeNativeStart;
            Epoch = epoch;
            Certificate = certificate;
            Root = root;
            Pins = pins;
            AcceptedValidator = acceptedValidator;
            DedicatedIssuerEpoch = dedicatedIssuerEpoch;
            Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }

        public long Generation { get; }
        public Guid? CredentialId { get; }
        public long CredentialRevision { get; }
        public bool Quarantined => Continuation is not null || RestartReplay is not null || PendingBind is not null && !PendingConfirmed;
        public string? ContinuationReference => Continuation?.Reference;
        public X509Certificate2 Certificate { get; }
        public string Fingerprint { get; }
        public Guid? IssuerEpoch => DedicatedIssuerEpoch;
        internal long Epoch { get; }
        internal X509Certificate2 Root { get; }
        internal HashSet<string> Pins { get; }
        internal Func<X509Certificate2, bool> AcceptedValidator { get; }
        internal Guid? DedicatedIssuerEpoch { get; }
        internal LocalVoterCredentialReplay? RestartReplay { get; }
        internal LocalVoterPendingBindReplay? PendingBind { get; }
        internal bool PendingConfirmed { get; }
        internal LocalVoterContinuationQuarantine? Continuation { get; }
        internal ManagedContinuationCut? BeforeNativeStart { get; }
    }
}
