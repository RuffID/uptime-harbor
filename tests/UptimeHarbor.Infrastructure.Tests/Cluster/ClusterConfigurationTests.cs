using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ClusterConfigurationTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("http")]
    [InlineData("protocol")]
    [InlineData("timing")]
    [InlineData("proxy")]
    [InlineData("limit")]
    public void EnabledOptionsRejectUnsafeConfiguration(string invalid)
    {
        // Arrange
        ClusterOptions options = Valid();
        switch (invalid)
        {
            case "identity":
                options.NodeId = Guid.Empty;
                break;
            case "http":
                options.PublicUri = "http://peer.example/internal/raft";
                break;
            case "protocol":
                options.ProtocolMinor = 1;
                break;
            case "timing":
                options.FailureWindowSeconds = 60;
                break;
            case "proxy":
                options.TlsBoundary = "NginxIsolatedHttp";
                break;
            case "limit":
                options.CommandMaxBytes = 262145;
                break;
        }

        // Act
        bool failed = new ClusterOptionsValidator().Validate(null, options).Failed;

        // Assert
        Assert.True(failed);
    }

    [Fact]
    public void ValidOptionsAndDisabledModeAreAccepted()
    {
        // Arrange
        ClusterOptionsValidator validator = new();

        // Act
        bool enabled = validator.Validate(null, Valid()).Succeeded;
        bool disabled = validator.Validate(null, new()).Succeeded;

        // Assert
        Assert.True(enabled);
        Assert.True(disabled);
    }

    [Fact]
    public void NativeIdentityMustMatchTrustedAddressAndEnvelopeTerm()
    {
        // Arrange
        PeerCredential credential = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Uri("https://peer.example/internal/raft"), new string('A', 64), true, false, DateTimeOffset.UtcNow.AddMinutes(1));
        string sender = ClusterMemberId.FromEndPoint(new UriEndPoint(credential.Address)).ToString();
        PeerEnvelope envelope = new(Guid.NewGuid(), 1, credential.NodeId, Guid.NewGuid(), credential.StorageGeneration,
            credential.CredentialId, Guid.NewGuid(), DateTimeOffset.UtcNow, "1.0/1/1/1/1/6.10.0", Guid.NewGuid(), 7);

        // Act
        bool valid = NativeRaftIdentity.Validate(credential, sender, "AppendEntries", "7", "1", envelope);
        bool wrongSender = NativeRaftIdentity.Validate(credential, "untrusted", "AppendEntries", "7", "1", envelope);
        bool wrongTerm = NativeRaftIdentity.Validate(credential, sender, "AppendEntries", "8", "1", envelope);
        bool admissionVote = NativeRaftIdentity.Validate(credential with { IsVoting = false, IsAdmission = true },
            sender, "RequestVote", "7", "1", envelope);

        // Assert
        Assert.True(valid);
        Assert.False(wrongSender);
        Assert.False(wrongTerm);
        Assert.False(admissionVote);
    }

    private static ClusterOptions Valid()
    {
        string root = Path.GetTempPath();
        return new() { Mode = "Bootstrap", ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid(),
            StorageGeneration = Guid.NewGuid(), CredentialId = Guid.NewGuid(), PublicUri = "https://peer.example/internal/raft",
            StoragePath = root, ClientCertificatePath = Path.Combine(root, "client.crt"),
            ClientKeyPath = Path.Combine(root, "client.key"), ClientCaPath = Path.Combine(root, "ca.crt"),
            ClientCaFingerprint = new string('A', 64), ExpectedBotId = 123 };
    }
}
