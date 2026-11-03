using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentPeerTrustTests
{
    [Fact]
    public async Task AcceptedIssuerRevocationClosesExistingRpcTrustWhileRetirementRetainsValidLeaf()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionStateStore state = new();
        RevisionImage image = fixture.Accepted();
        state.Publish(image);
        AcceptedPeerTrustSource source = new(state, new FixedTime(fixture.Crypto.Now));
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![fixture.Crypto.Epoch];

        // Act
        IReadOnlyList<PeerCredential> active = await source.ListAsync(TestContext.Current.CancellationToken);
        state.Publish(image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RetiredAtUtc = fixture.Crypto.Now })
        } });
        IReadOnlyList<PeerCredential> retired = await source.ListAsync(TestContext.Current.CancellationToken);
        state.Publish(image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = fixture.Crypto.Now })
        } });
        IReadOnlyList<PeerCredential> revoked = await source.ListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(active);
        Assert.Single(retired);
        Assert.Empty(revoked);
    }

    private class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
