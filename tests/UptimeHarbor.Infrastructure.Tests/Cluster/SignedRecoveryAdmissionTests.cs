using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class SignedRecoveryAdmissionTests
{
    [Fact]
    public async Task SignedBootstrapUsesExactProtectedCommandAndTrustedLocalPeerIdentity()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RecoveryAdmission proof = Prepare(fixture, out RevisionCommand bootstrap);
        SignedRecoveryAdmission source = new(fixture.Control, Options.Create(fixture.Options), fixture.Time);

        // Act
        RecoveryAdmission? admitted = await source.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(admitted);
        Assert.Equal(RevisionCodec.Digest(bootstrap), RevisionCodec.Digest(admitted.BootstrapAccount!));
        Assert.Single(admitted.Operations.Nodes);
        Assert.False(admitted.Operations.Nodes[fixture.Options.NodeId].Activated);
        Assert.Equal(123, admitted.Operations.BotId);
        Assert.Equal(proof.ClusterId, admitted.ClusterId);
        string envelope = await File.ReadAllTextAsync(Path.Combine(fixture.Configuration.Directory, "admission.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("protected-hash", envelope);
    }

    [Theory]
    [InlineData("Join")]
    [InlineData("Restart")]
    public async Task JoinAndRestartCannotCreateIndependentAdministrator(string mode)
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        Prepare(fixture, out _);
        fixture.Options.Mode = mode;
        SignedRecoveryAdmission source = new(fixture.Control, Options.Create(fixture.Options), fixture.Time);

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await source.ReadAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    [Fact]
    public async Task PlainFencingFlagWithoutManualIsolationIsInsufficient()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RecoveryAdmission proof = Prepare(fixture, out _);
        fixture.Sign("admission.json", fixture.Request("Admission") with { Admission = proof, IsolationEvidenceDigest = new string('A', 64) });
        SignedRecoveryAdmission source = new(fixture.Control, Options.Create(fixture.Options), fixture.Time);

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await source.ReadAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    [Fact]
    public async Task CopiedAdmissionFailsInNewProcessAndWrongCertificateCannotBecomeReady()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RecoveryAdmission proof = Prepare(fixture, out _);
        SignedRecoveryAdmission clone = new(fixture.NewControl(), Options.Create(fixture.Options), fixture.Time);

        // Act
        RecoveryAdmission? copied = await clone.ReadAsync(TestContext.Current.CancellationToken);
        fixture.Options.ClientCaFingerprint = new string('0', 64);
        SignedRecoveryAdmission wrongIdentity = new(fixture.Control, Options.Create(fixture.Options), fixture.Time);
        Exception? failure = await Record.ExceptionAsync(async () => await wrongIdentity.ReadAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(copied);
        Assert.IsType<InvalidDataException>(failure);
        Assert.True(proof.FencingConfirmed);
    }

    private static RecoveryAdmission Prepare(ClusterControlFixture fixture, out RevisionCommand command)
    {
        using RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new("CN=Test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(fixture.Time.Now.AddDays(-1), fixture.Time.Now.AddDays(1));
        using RSA key = RSA.Create(2048);
        CertificateRequest leafRequest = new("CN=First node", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri($"urn:uptimeharbor:{fixture.Options.ClusterId:D}:1:{fixture.Options.NodeId:D}"));
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using X509Certificate2 certificate = leafRequest.Create(root, fixture.Time.Now.AddMinutes(-1), fixture.Time.Now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        fixture.Options.TlsBoundary = "NginxIsolatedHttp";
        fixture.Options.ClientCaFingerprint = root.GetCertHashString(HashAlgorithmName.SHA256);
        fixture.Options.ClientCaPath = Path.Combine(fixture.Directory.Location, "ca.pem");
        fixture.Options.ClientCertificatePath = Path.Combine(fixture.Directory.Location, "peer.pem");
        fixture.Options.ClientKeyPath = Path.Combine(fixture.Directory.Location, "peer-key.pem");
        File.WriteAllText(fixture.Options.ClientCaPath, root.ExportCertificatePem());
        File.WriteAllText(fixture.Options.ClientCertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(fixture.Options.ClientKeyPath, key.ExportPkcs8PrivateKeyPem());
        string nativeId = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(fixture.Options.PublicUri))).ToString();
        OperationalNode node = new(fixture.Options.NodeId, fixture.Options.StorageGeneration, fixture.Options.PublicUri, nativeId, true, false, false, 0);
        OperationalCredential credential = new(fixture.Options.CredentialId, node.NodeId, certificate.GetCertHashString(HashAlgorithmName.SHA256),
            certificate.ExportCertificatePem(), fixture.Time.Now.AddHours(1), null, null);
        ClusterOperationsImage operations = new()
        {
            Nodes = new ClusterOperationsImage().Nodes.Add(node.NodeId, node),
            Credentials = new ClusterOperationsImage().Credentials.Add(credential.Id, credential), BotId = 123
        };
        Guid account = Guid.NewGuid();
        command = new()
        {
            ClusterId = fixture.Options.ClusterId, ClusterGeneration = 1, OriginNodeId = node.NodeId,
            OperationId = Guid.NewGuid(), ActorUserId = account, ActorRevision = 1, EntityId = account,
            EntityKind = DeletedEntityKind.Account, Action = "BootstrapAccount", BaseRevision = 0, CreatedAtUtc = fixture.Time.Now,
            Value = new() { Name = "admin", RoleId = Guid.NewGuid(), PasswordHash = "protected-hash", SecurityStamp = "stamp", ConcurrencyStamp = "concurrency" }
        };
        fixture.Configuration.KeyRingDirectory = Path.Combine(fixture.Directory.Location, "keys");
        IDataProtectionProvider protection = DataProtectionProvider.Create(new DirectoryInfo(fixture.Configuration.KeyRingDirectory),
            builder => builder.SetApplicationName($"UptimeHarbor.ClusterControl/{node.NodeId:N}"));
        string encrypted = protection.CreateProtector("UptimeHarbor.Control.Bootstrap.v1").Protect(Encoding.UTF8.GetString(RevisionCodec.Encode(command)));
        RecoveryAdmission proof = new(fixture.Options.ClusterId, 1, node.NodeId, node.StorageGeneration, true, false, null, null, 0, 0)
        {
            Operations = operations, MonitoringNodes = new MonitoringImage().Nodes.Add(node.NodeId, node.StorageGeneration)
        };
        ManualIsolationConfirmation isolation = new(Guid.NewGuid(), Guid.NewGuid(), fixture.Time.Now,
            [new(node.NodeId, node.StorageGeneration, true, true, true, true, true, new string('A', 64))], 123, false, false, new string('B', 64));
        fixture.Sign("admission.json", fixture.Request("Admission") with
        {
            Admission = proof, Isolation = isolation, ProtectedBootstrapCommand = encrypted,
            IsolationEvidenceDigest = ManualIsolationValidation.Validate(isolation, proof.MonitoringNodes, fixture.Time.Now, 123, false)
        });
        Directory.CreateDirectory(fixture.Options.StoragePath);
        File.WriteAllBytes(Path.Combine(fixture.Options.StoragePath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(
            new ClusterDiskManifest(fixture.Options.ClusterId, 1, node.NodeId, node.StorageGeneration, node.Address, Guid.NewGuid())));
        return proof;
    }
}
