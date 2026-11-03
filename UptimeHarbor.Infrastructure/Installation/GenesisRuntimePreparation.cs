using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Сверяет durable intent/SQL refs/файлы, затем готовит только карантинный singleton.</summary>
public class GenesisRuntimePreparation(ILocalSetupUnitOfWork work, IEnrollmentSigningRepository signing,
    LocalSecretStore secrets, InstallationState installation, InstallationLease lease, DynamicPeerTls tls,
    ManagedAdmissionState admission, GenesisWriterPermit permit, RevisionStateStore accepted,
    IOptions<ClusterOptions> configured, TimeProvider time)
{
    public async Task<bool> PrepareAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (profile != installation.Current || profile.Mode != "Bootstrap" || profile.BootId != lease.BootId
            || profile.Phase != InstallationPhase.ClusterQuarantine || profile.Evidence is null
            || !LocalProfileEquality.Same(await work.Profiles.ReadAsync(token), profile))
        {
            return false;
        }

        SetupAuthorityEvidence evidence = JsonSerializer.Deserialize<SetupAuthorityEvidence>(profile.Evidence)
            ?? throw new InvalidDataException("GenesisIntentMissing");
        PreparedGenesisValidation.Validate(profile, evidence, time.GetUtcNow());
        PreparedGenesis prepared = evidence.Genesis!;
        IReadOnlyList<EnrollmentIssuerReference> issuers = await signing.ListIssuersAsync(token);
        secrets.ReconcileAll(profile, issuers);
        EnrollmentIssuerReference issuer = issuers.Single(item => item.Epoch == prepared.Template.IssuerEpoch);
        EnrollmentIssuedArtifact artifact = await signing.FindIssueAsync(prepared.Template.IssueId, token)
            ?? throw new InvalidDataException("GenesisIssuanceMissing");
        EnrollmentTrustCommand data = prepared.InitializeCluster.EnrollmentTrust!;
        bool exact = issuer.CertificateDigest == prepared.Authorization.IssuerCertificateDigest
            && issuer.SpkiDigest == prepared.Authorization.IssuerSpkiDigest
            && Convert.ToBase64String(issuer.CertificateDer) == data.Issuer!.CertificateDer
            && artifact.GenesisId == prepared.Authorization.GenesisId && artifact.AuthorizationIndex == 0
            && artifact.AttemptId == profile.AttemptId && artifact.IssuerEpoch == issuer.Epoch
            && artifact.IssueId == prepared.Template.IssueId && artifact.RequestDigest == EnrollmentRequestCodec.Digest(prepared.Request)
            && artifact.TemplateDigest == EnrollmentSigningJournal.TemplateDigest(prepared.Template)
            && artifact.CertificateDigest == data.Credential!.Fingerprint
            && Convert.ToBase64String(artifact.CertificateDer) == data.Credential.PublicCertificate;
        if (!exact)
        {
            throw new InvalidDataException("GenesisJournalBindingInvalid");
        }

        RevisionCommand account = ReadAccount(profile);
        string storage = configured.Value.StoragePath;
        PrepareManifest(storage, profile, prepared.Authorization);
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(issuer.CertificateDer);
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        byte[] keyBytes = secrets.Read(profile.AttemptId!.Value, "peer", profile.KeyReference!);
        X509Certificate2 localCertificate;
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(keyBytes));
            localCertificate = leaf.CopyWithPrivateKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }

        RecoveryAdmission proof = new(profile.ClusterId!.Value, profile.ClusterGeneration!.Value, profile.NodeId!.Value,
            profile.StorageGeneration!.Value, false, false, null, null, 0, 0)
        {
            BootstrapAccount = account,
            Proof = new FreshBootstrapProof(prepared.Authorization, prepared.InitializeCluster, lease.BootId, lease.BootChallenge)
        };
        permit.Arm(prepared.Authorization, prepared.InitializeCluster, account);
        admission.Publish(profile, proof);
        // До InitializeCluster trust callback разрешает только exact local genesis leaf; не произвольный CA leaf.
        tls.Install(profile.RuntimeGeneration, localCertificate, new X509Certificate2(root), [artifact.CertificateDigest],
            certificate => CurrentCredential(certificate, prepared), dedicatedIssuerEpoch: prepared.Template.IssuerEpoch);
        return true;
    }

    private bool CurrentCredential(X509Certificate2 certificate, PreparedGenesis prepared)
    {
        string digest = EnrollmentCertificates.Hash(certificate.RawData);
        RevisionImage? image = accepted.Current;
        if (image?.OnboardingContracts?.GenesisInitialized == true)
        {
            OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(prepared.Template.IssueId);
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(prepared.Request.NodeId);
            AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts.Issuers?.GetValueOrDefault(prepared.Template.IssuerEpoch);
            return credential is not null && credential.Fingerprint == digest && !credential.RevokedAtUtc.HasValue
                && time.GetUtcNow() < credential.ExpiresAtUtc && node is not null && !node.Removed
                && node.StorageGeneration == prepared.Request.StorageGeneration && issuer is not null && !issuer.RevokedAtUtc.HasValue;
        }

        return admission.Read()?.Proof is FreshBootstrapProof proof && proof.Authorization == prepared.Authorization
            && digest == prepared.InitializeCluster.EnrollmentTrust!.Credential!.Fingerprint;
    }

    private RevisionCommand ReadAccount(LocalInstallationProfile profile)
    {
        byte[] bytes = secrets.Read(profile.AttemptId!.Value, "bootstrap", profile.BootstrapAccountReference!);
        try
        {
            RevisionCommand command = RevisionCodec.Decode(bytes);
            if (command.Action != "BootstrapAccount" || RevisionCodec.Digest(command) != profile.BootstrapAccountDigest
                || command.OperationId != profile.OperationIds!["bootstrap"])
            {
                throw new InvalidDataException("GenesisAccountChanged");
            }

            return command;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void PrepareManifest(string storage, LocalInstallationProfile profile, GenesisAuthorization genesis)
    {
        ProtectedInstallationFiles.VerifyPermissions(storage, true);
        if (File.Exists(Path.Combine(storage, "started")))
        {
            // Не превращать частичный/повторный запуск в ColdStart. Resume требует отдельного проверенного proof.
            throw new InvalidDataException("GenesisStartedRequiresProcessResume");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(storage))
        {
            if (Path.GetFileName(entry) is not ("installation.lock" or "manifest.json"))
            {
                throw new InvalidDataException("GenesisStorageNotFresh");
            }
        }

        ClusterDiskManifest expected = new(genesis.ClusterId, genesis.Generation, genesis.NodeId,
            genesis.StorageGeneration, genesis.PeerUri, genesis.GenesisId);
        string path = Path.Combine(storage, "manifest.json");
        if (File.Exists(path))
        {
            ProtectedInstallationFiles.VerifyPermissions(path);
            ClusterDiskManifest? existing = JsonSerializer.Deserialize<ClusterDiskManifest>(File.ReadAllBytes(path));
            if (existing != expected || profile.InstallIdentity != genesis.InstallIdentity)
            {
                throw new InvalidDataException("GenesisManifestChanged");
            }

            return;
        }

        ProtectedInstallationFiles.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(expected));
    }
}
