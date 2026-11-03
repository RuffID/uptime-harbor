using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Создаёт только точный local genesis. SQL refs сохраняются coordinator вместе с evidence в одной транзакции.</summary>
public class GenesisPreparation(ILocalSetupUnitOfWork work, IEnrollmentSigningRepository signing,
    LocalSecretStore secrets, InstallationLease lease, IOptions<InstallationOptions> installation, TimeProvider time)
{
    public async Task<SetupAuthorityEvidence> PrepareAsync(BootstrapPreparation request, CancellationToken token)
    {
        LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
            ?? throw new InvalidDataException("InstallationProfileMissing");
        if (profile.Mode != "Bootstrap" || profile.Phase != InstallationPhase.LocalPrepared
            || profile.InstallIdentity != lease.InstallIdentity || profile.InstallIdentity != request.InstallIdentity
            || profile.BootId != lease.BootId || profile.AttemptId != request.AttemptId
            || profile.ClusterId != request.ClusterId || profile.ClusterGeneration != 1 || profile.NodeId != request.NodeId
            || profile.StorageGeneration != request.StorageGeneration || profile.PeerAddress != request.PeerAddress
            || profile.BootstrapAccountDigest != request.AccountDigest || profile.BootstrapAccountReference != request.AccountReference
            || profile.BotId != request.BotId || request.BotId <= 0 || profile.KeyReference is null)
        {
            throw new InvalidDataException("GenesisPreparationIdentityMismatch");
        }

        if (profile.Evidence is not null)
        {
            SetupAuthorityEvidence existing = JsonSerializer.Deserialize<SetupAuthorityEvidence>(profile.Evidence)
                ?? throw new InvalidDataException("GenesisEvidenceMissing");
            PreparedGenesisValidation.Validate(profile, existing, time.GetUtcNow());
            return existing with { RuntimeGeneration = profile.RuntimeGeneration };
        }

        new SetupPeerAddressPolicy(installation.Value).ValidateUri(request.PeerAddress);
        RevisionCommand account = ReadAccount(profile);
        DateTimeOffset now = EnrollmentCertificates.Seconds(time.GetUtcNow());
        Guid epoch = Guid.NewGuid();
        Guid credentialId = profile.OperationIds!["admit"];
        using ECDsa peerKey = ReadPeerKey(profile);
        byte[] csr = new CertificateRequest($"CN=UptimeHarbor {request.NodeId:D}", peerKey, HashAlgorithmName.SHA256)
            .CreateSigningRequest();
        EnrollmentRequest enrollment = new(1, request.ClusterId, 1, request.AttemptId, request.AttemptId,
            request.NodeId, request.StorageGeneration, request.PeerAddress,
            EnrollmentCertificates.Hash(peerKey.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr));
        using ECDsa issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 issuer = EnrollmentCertificates.CreateIssuer(issuerKey, request.ClusterId, 1, epoch, now);
        byte[] privatePem = Encoding.UTF8.GetBytes(issuerKey.ExportPkcs8PrivateKeyPem());
        string keyReference;
        try
        {
            keyReference = secrets.Write(epoch, "issuer", privatePem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privatePem);
        }

        EnrollmentIssuerReference localIssuer = new(epoch, profile.InstallIdentity, request.ClusterId, 1, request.NodeId,
            request.StorageGeneration, keyReference, issuer.RawData, EnrollmentCertificates.Hash(issuer.RawData),
            EnrollmentCertificates.Hash(issuerKey.ExportSubjectPublicKeyInfo()));
        // Стабильный genesis ID отличается от уже сохранённого BootstrapAccount OperationId.
        GenesisAuthorization genesis = new(1, request.AttemptId, request.InstallIdentity, request.ClusterId, 1,
            request.NodeId, request.StorageGeneration, request.PeerAddress, localIssuer.SpkiDigest,
            localIssuer.CertificateDigest, request.AccountDigest, null);
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 1);
        EnrollmentIssuance template = new(1, credentialId, epoch, Convert.ToHexString(serial),
            request.NodeId, request.StorageGeneration, request.PeerAddress, enrollment.SpkiDigest, enrollment.CsrDigest, now, now.AddDays(30));
        using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(issuer, enrollment, template, csr);
        EnrollmentIssuedArtifact artifact = new(credentialId, epoch, request.AttemptId, EnrollmentRequestCodec.Digest(enrollment),
            EnrollmentSigningJournal.TemplateDigest(template), genesis.GenesisId, 0, leaf.RawData, EnrollmentCertificates.Hash(leaf.RawData));
        OperationalNode node = new(request.NodeId, request.StorageGeneration, request.PeerAddress,
            ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(request.PeerAddress))).ToString(), true, false, false, 0);
        OperationalCredential credential = new(credentialId, request.NodeId, artifact.CertificateDigest,
            Convert.ToBase64String(artifact.CertificateDer), template.NotAfterUtc, null, null);
        AcceptedEnrollmentIssuer anchor = new(epoch, request.NodeId, request.StorageGeneration, EnrollmentIssuer.PEER_PURPOSE,
            Convert.ToBase64String(issuer.RawData), localIssuer.CertificateDigest, localIssuer.SpkiDigest, now, now.AddYears(3), 0);
        RevisionCommand initialize = new()
        {
            Schema = 3, ClusterId = request.ClusterId, ClusterGeneration = 1, OperationId = genesis.GenesisId,
            OriginNodeId = request.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0, EntityId = epoch,
            EntityKind = DeletedEntityKind.Node, Action = "InitializeCluster", SystemSource = "GenesisController",
            CreatedAtUtc = account.CreatedAtUtc, BaseRevision = 0,
            EnrollmentTrust = new(1, request.StorageGeneration, epoch, anchor, genesis, node, credential, request.BotId, null)
        };
        SetupAuthorityEvidence evidence = new(request.AttemptId, request.RuntimeGeneration, 0, request.AccountDigest,
            SetupAuthorityEvidenceKind.GenesisPrepared)
        {
            Genesis = new(genesis, initialize, enrollment, template, csr)
        };
        PreparedGenesisValidation.Validate(profile, evidence, time.GetUtcNow());
        // Ни одного SaveChanges здесь: общий scoped tracker входит в coordinator transaction.
        signing.AddIssuer(localIssuer);
        signing.AddIssue(artifact);
        return evidence;
    }

    private RevisionCommand ReadAccount(LocalInstallationProfile profile)
    {
        byte[] bytes = secrets.Read(profile.AttemptId!.Value, "bootstrap", profile.BootstrapAccountReference!);
        try
        {
            RevisionCommand account = RevisionCodec.Decode(bytes);
            if (account.Action != "BootstrapAccount" || RevisionCodec.Digest(account) != profile.BootstrapAccountDigest
                || account.OperationId != profile.OperationIds!["bootstrap"] || account.OriginNodeId != profile.NodeId
                || account.ClusterId != profile.ClusterId || account.ClusterGeneration != profile.ClusterGeneration)
            {
                throw new InvalidDataException("GenesisAccountChanged");
            }

            return account;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private ECDsa ReadPeerKey(LocalInstallationProfile profile)
    {
        byte[] bytes = secrets.Read(profile.AttemptId!.Value, "peer", profile.KeyReference!);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(bytes));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
