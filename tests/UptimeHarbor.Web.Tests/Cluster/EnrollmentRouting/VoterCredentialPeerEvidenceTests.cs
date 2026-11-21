using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialPeerEvidenceTests
{
    [Theory]
    [InlineData("previous", true)]
    [InlineData("replacement", true)]
    [InlineData("overlap-boundary", false)]
    [InlineData("wrong-server-id", false)]
    [InlineData("wrong-server-leaf", false)]
    [InlineData("wrong-client-leaf", false)]
    [InlineData("revoked-credential", false)]
    public async Task IssuerChecksBothAuthenticatedLeavesAgainstFreshAcceptedEvidence(string scenario, bool allowed)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        DateTimeOffset overlapEnd = fixture.Cluster.First.Time.Now.AddSeconds(1);
        using X509Certificate2 next = fixture.BindIssuerReplacement(overlapEnd);
        RevisionImage image = fixture.Cluster.State.Current!;
        var local = fixture.Issuer.Options;
        var subject = fixture.Subject.Options;
        PeerCredential presentedClient = Assert.IsType<PeerCredential>(await
            new AcceptedPeerTrustSource(fixture.Cluster.State, fixture.Cluster.First.Time).FindAsync(subject.CredentialId, token));
        string nextFingerprint = next.GetCertHashString(HashAlgorithmName.SHA256);
        Guid nextId = image.Operations.Credentials.Values.Single(item => item.Fingerprint == nextFingerprint).Id;
        Guid serverId = scenario is "replacement" or "wrong-server-leaf" ? nextId : local.CredentialId;
        string serverFingerprint = scenario is "replacement" or "wrong-server-id" ? nextFingerprint
            : fixture.Issuer.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (scenario == "overlap-boundary")
        {
            fixture.Cluster.First.Time.Now = overlapEnd;
        }
        else if (scenario == "wrong-client-leaf")
        {
            presentedClient = presentedClient with { Fingerprint = nextFingerprint };
        }
        else if (scenario == "revoked-credential")
        {
            OperationalCredential old = image.Operations.Credentials[serverId];
            fixture.Publish(image with
            {
                Operations = image.Operations with { Credentials = image.Operations.Credentials.SetItem(old.Id,
                    old with { RevokedAtUtc = fixture.Cluster.First.Time.Now }) }
            });
        }

        PeerEnvelope peer = new(subject.ClusterId, subject.Generation, subject.NodeId, local.NodeId,
            subject.StorageGeneration, subject.CredentialId, Guid.NewGuid(), fixture.Cluster.First.Time.Now,
            subject.Compatibility, Guid.NewGuid(), fixture.Cluster.Term);

        // Act
        EnrollmentIssuedArtifact? artifact = null;
        Exception? failure = await Record.ExceptionAsync(async () => artifact = await fixture.Issuer.Renewal
            .IssueAsync(fixture.Call, peer, presentedClient, token, serverId, serverFingerprint));

        // Assert
        if (allowed)
        {
            Assert.Null(failure);
            Assert.Equal(fixture.IssueId, artifact!.IssueId);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(failure);
        }

        Assert.Equal(fixture.InitialIssueCount + (allowed ? 1 : 0), fixture.Issuer.Store.Issues.Count);
    }
}
