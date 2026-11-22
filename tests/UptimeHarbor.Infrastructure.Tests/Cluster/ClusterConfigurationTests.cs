using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
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
    public void ManualRosterSuppliesLocalAddressAndRestrictsTelegramToFirstTwoNodes()
    {
        // Arrange
        ClusterOptions options = WithManualNodes();

        // Act
        ValidateOptionsResult validation = new ClusterOptionsValidator().Validate(null, options);

        // Assert
        Assert.True(validation.Succeeded, validation.FailureMessage);
        Assert.Equal(options.Nodes[1].Address, options.PublicUri);
        Assert.True(options.CanAssignTelegramSender(options.Nodes[0].NodeId));
        Assert.True(options.CanAssignTelegramSender(options.Nodes[1].NodeId));
        Assert.False(options.CanAssignTelegramSender(options.Nodes[2].NodeId));
    }

    [Theory]
    [InlineData("local", "Cluster:NodeId")]
    [InlineData("number", "numbers")]
    [InlineData("id", "unique non-empty NodeId")]
    [InlineData("address", "unique canonical HTTPS address")]
    [InlineData("public", "Cluster:PublicUri")]
    [InlineData("certificate", "Cluster:ClientCertificatePath")]
    [InlineData("count", "up to three")]
    public void InvalidManualRosterNamesTheProblem(string invalid, string expected)
    {
        // Arrange
        ClusterOptions options = WithManualNodes();
        switch (invalid)
        {
            case "local":
                options.NodeId = Guid.NewGuid();
                break;
            case "number":
                options.Nodes[2].Number = 2;
                break;
            case "id":
                options.Nodes[2].NodeId = options.Nodes[0].NodeId;
                break;
            case "address":
                options.Nodes[2].Address = options.Nodes[0].Address;
                break;
            case "public":
                options.PublicUri = options.Nodes[0].Address;
                break;
            case "certificate":
                options.ClientCertificatePath = "peer.pem";
                break;
            case "count":
                options.Nodes = [..options.Nodes, new()];
                break;
        }

        // Act
        ValidateOptionsResult validation = new ClusterOptionsValidator().Validate(null, options);

        // Assert
        Assert.True(validation.Failed);
        Assert.Contains(expected, validation.FailureMessage);
    }

    private static ClusterOptions WithManualNodes()
    {
        ClusterOptions options = Valid();
        options.PublicUri = string.Empty;
        options.Nodes =
        [
            new() { Number = 1, NodeId = Guid.NewGuid(), Address = "https://uh1.example/internal/raft" },
            new() { Number = 2, NodeId = options.NodeId, Address = "https://uh2.example/internal/raft" },
            new() { Number = 3, NodeId = Guid.NewGuid(), Address = "https://uh3.example/internal/raft" }
        ];

        return options;
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
