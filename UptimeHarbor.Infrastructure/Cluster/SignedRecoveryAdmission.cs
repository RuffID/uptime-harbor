using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Допуск из аутентифицированной внешней процедуры; пустая БД не создаёт разрешение.</summary>
public class SignedRecoveryAdmission(ClusterLocalControl control, IOptions<ClusterOptions> options,
    TimeProvider time) : IRecoveryAdmission
{
    private RecoveryAdmission? admitted;

    public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClusterControlRequest? request = control.Read("admission.json", "Admission");
        if (request is null)
        {
            return ValueTask.FromResult<RecoveryAdmission?>(null);
        }

        RecoveryAdmission proof = request.Admission ?? throw new InvalidDataException("AdmissionMissing");
        ClusterOptions node = options.Value;
        bool identity = proof.ClusterId == node.ClusterId && proof.Generation == node.Generation
            && proof.NodeId == node.NodeId && proof.StorageGeneration == node.StorageGeneration;
        if (!identity || !proof.FencingConfirmed || !ClusterOptionsValidator.IsFingerprint(request.IsolationEvidenceDigest))
        {
            throw new InvalidDataException("AdmissionEvidenceMissing");
        }

        ManualIsolationConfirmation isolation = request.Isolation ?? throw new InvalidDataException("ManualIsolationMissing");
        Dictionary<Guid, Guid> localInstance = new() { [node.NodeId] = node.StorageGeneration };
        string evidence = ManualIsolationValidation.Validate(isolation, localInstance, time.GetUtcNow(), node.ExpectedBotId, false);
        if (evidence != request.IsolationEvidenceDigest)
        {
            throw new InvalidDataException("IsolationConfirmationMismatch");
        }

        if (proof.BootstrapAccount is not null)
        {
            throw new InvalidDataException("UnprotectedBootstrapCommand");
        }

        if (request.ProtectedBootstrapCommand is not null)
        {
            if (node.Mode != "Bootstrap" || proof.OldBackup || proof.SnapshotId.HasValue)
            {
                throw new InvalidDataException("UnexpectedIndependentBootstrap");
            }

            string json = control.UnprotectBootstrap(request.ProtectedBootstrapCommand);
            RevisionCommand command = RevisionCodec.Decode(System.Text.Encoding.UTF8.GetBytes(json));
            proof = proof with { BootstrapAccount = command };
        }

        ValidateRegistry(proof, node);
        admitted = proof;
        return ValueTask.FromResult<RecoveryAdmission?>(admitted);
    }

    private void ValidateRegistry(RecoveryAdmission proof, ClusterOptions options)
    {
        OperationalNode? local = proof.Operations.Nodes.GetValueOrDefault(options.NodeId);
        OperationalCredential? credential = proof.Operations.Credentials.GetValueOrDefault(options.CredentialId);
        if (local is null || local.StorageGeneration != options.StorageGeneration || local.Address != options.PublicUri
            || credential is null || credential.NodeId != local.NodeId || credential.RevokedAtUtc.HasValue
            || proof.Operations.BotId != options.ExpectedBotId)
        {
            throw new InvalidDataException("AdmissionIdentityNotTrusted");
        }

        if (proof.Operations.MembershipVersion != proof.MembershipVersion || local.Removed
            || proof.Operations.Nodes.Any(item => item.Key != item.Value.NodeId || item.Value.NodeId == Guid.Empty
                || item.Value.StorageGeneration == Guid.Empty)
            || proof.Operations.Nodes.Values.Select(item => item.StorageGeneration).Distinct().Count() != proof.Operations.Nodes.Count
            || proof.Operations.Credentials.Any(item => item.Key != item.Value.Id || !proof.Operations.Nodes.ContainsKey(item.Value.NodeId)))
        {
            throw new InvalidDataException("AdmissionRegistryMismatch");
        }

        foreach (OperationalNode member in proof.Operations.Nodes.Values)
        {
            ClusterOperationValidation.ValidateAddress(member.Address);
            if (member.RaftMemberId != ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(member.Address))).ToString())
            {
                throw new InvalidDataException("AdmissionNativeIdentityMismatch");
            }
        }

        using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath);
        using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);
        PeerCredential peer = new(credential.Id, local.NodeId, local.StorageGeneration, new Uri(local.Address),
            credential.Fingerprint, local.Voting, credential.AdmissionId.HasValue, credential.ExpiresAtUtc);
        if (!new PeerCertificateValidator().Validate(certificate, root, options, peer, time.GetUtcNow()))
        {
            throw new InvalidDataException("LocalPeerIdentityUnconfirmed");
        }

        if (proof.Operations.Nodes.Values.Where(item => !item.Removed).Select(item => item.Address).Distinct(StringComparer.Ordinal).Count()
            != proof.Operations.Nodes.Values.Count(item => !item.Removed) || !proof.MonitoringNodes.OrderBy(item => item.Key)
                .SequenceEqual(proof.Operations.Nodes.ToDictionary(item => item.Key, item => item.Value.StorageGeneration).OrderBy(item => item.Key)))
        {
            throw new InvalidDataException("AdmissionRegistryMismatch");
        }

        string manifestPath = Path.Combine(options.StoragePath, "manifest.json");
        if (options.Mode == "Join" && (!proof.SnapshotId.HasValue || proof.MinimumCommittedIndex <= 0 || local.Voting))
        {
            throw new InvalidDataException("EmptyJoinRequiresAdmittedSnapshot");
        }

        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("ProvisionedManifestMissing");
        }

        ClusterDiskManifest manifest = JsonSerializer.Deserialize<ClusterDiskManifest>(File.ReadAllBytes(manifestPath))
            ?? throw new InvalidDataException("ProvisionedManifestMissing");
        if (manifest.ClusterId != options.ClusterId || manifest.Generation != options.Generation
            || manifest.NodeId != options.NodeId || manifest.StorageGeneration != options.StorageGeneration
            || manifest.PublicUri != options.PublicUri)
        {
            throw new InvalidDataException("ProvisionedIdentityMismatch");
        }

        if (options.Mode == "Bootstrap")
        {
            bool singleton = proof.Operations.Nodes.Values.Count(item => item.Voting && !item.Removed) == 1 && local.Voting;
            bool fresh = manifest.RecoveryId == Guid.Empty && proof.Operations.Nodes.Count == 1
                && proof.BootstrapAccount is not null && proof.MinimumCommittedIndex == 0 && !proof.SnapshotId.HasValue
                && !local.Activated && proof.Operations.Sender is null && proof.Operations.Changes.IsEmpty
                && proof.Operations.MaintenanceId == Guid.Empty;
            bool recovery = manifest.RecoveryId != Guid.Empty && proof.BootstrapAccount is null
                && proof.MinimumCommittedIndex > 0 && proof.SnapshotId.HasValue && proof.OldBackup;
            if (!singleton || (!fresh && !recovery))
            {
                throw new InvalidDataException("IndependentBootstrapForbidden");
            }
        }
    }
}
