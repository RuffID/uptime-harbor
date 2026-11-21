using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class RemoteGrantTests
{
    [Theory]
    [InlineData(OnlineGrantPurpose.Process)]
    [InlineData(OnlineGrantPurpose.Activation)]
    [InlineData(OnlineGrantPurpose.Enable)]
    public async Task RemoteFollowerSignsExactPurposeAfterDurableRequestWithoutCreatingAcceptedAuthority(OnlineGrantPurpose purpose)
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        OnlineGrant grant = fixture.Grant(purpose);
        RevisionImage before = fixture.Cluster.State.Current!;
        fixture.Cluster.BeforeSend = (_, path) =>
        {
            Assert.Equal(EnrollmentPeerProtocol.GRANT, path);
            Assert.True(fixture.Caller.Store.Steps.ContainsKey(OnlineGrantRequestSignatures.IntentId(grant.GrantId)));
        };

        // Act
        OnlineGrant signed = await fixture.Caller.Router.SignAsync(grant, token);

        // Assert
        Assert.False(fixture.Cluster.Issuer.View.Consensus!.IsLeader);
        Assert.Equal(grant, signed with { Signature = string.Empty });
        Assert.True(OnlineGrantSignatures.Verify(before, signed, fixture.Cluster.First.Time.Now));
        Assert.Same(before, fixture.Cluster.State.Current);
        Assert.False(before.OnboardingContracts!.OnlineGrants!.ContainsKey(grant.GrantId));
        OnlineEnableState gate = new(fixture.Cluster.State, fixture.Caller.Installation, fixture.Caller.Lease,
            fixture.Caller.Readiness, fixture.Cluster.First.Time);
        Assert.False(gate.Confirm(signed));
        Assert.False(gate.Allows());
        Assert.Single(fixture.Cluster.First.Store.Issues);
        Assert.Empty(fixture.Caller.Store.Issuers);
        var call = Assert.Single(fixture.Cluster.Sent);
        Assert.Equal(fixture.Cluster.Issuer.Options.NodeId, call.Target);
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(call.Body));
        OnlineGrantSigningRequest request = StrictOnboardingJson.Decode<OnlineGrantSigningRequest>(call.Body);
        Assert.Equal(grant, request.Grant);
        Assert.True(OnlineGrantRequestSignatures.Verify(before, request, fixture.Cluster.First.Time.Now));
    }

    [Theory]
    [InlineData("outage")]
    [InlineData("lost-response")]
    [InlineData("leader-change")]
    public async Task RetryUsesOriginalDurableGrantAndProofAfterSignerFailure(string boundary)
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        OnlineGrant grant = fixture.Grant();
        if (boundary == "outage")
        {
            fixture.Cluster.OfflineSigner = true;
        }
        else if (boundary == "lost-response")
        {
            fixture.Cluster.LoseResponse = EnrollmentPeerProtocol.GRANT;
        }
        else
        {
            fixture.Cluster.AfterSend = (_, _) =>
            {
                fixture.Cluster.AfterSend = null;
                fixture.Cluster.Term++;
                fixture.Cluster.SetLeader(fixture.Caller);
            };
        }

        // Act
        Exception? failed = await Record.ExceptionAsync(() => fixture.Caller.Router.SignAsync(grant, token));
        LocalAttemptStep intent = fixture.Caller.Store.Steps[OnlineGrantRequestSignatures.IntentId(grant.GrantId)];
        fixture.Cluster.OfflineSigner = false;
        fixture.Cluster.First.Time.Now = fixture.Cluster.First.Time.Now.AddSeconds(1);
        OnlineGrant retry = await fixture.Caller.Router.SignAsync(grant with
        {
            IssuedAtUtc = fixture.Cluster.First.Time.Now, ExpiresAtUtc = fixture.Cluster.First.Time.Now.AddMinutes(2)
        }, token);

        // Assert
        Assert.NotNull(failed);
        Assert.Equal(grant, retry with { Signature = string.Empty });
        Assert.Same(intent, fixture.Caller.Store.Steps[intent.OperationId]);
        Assert.Equal(fixture.Cluster.Sent[0].Body, fixture.Cluster.Sent[1].Body);
        Assert.Single(fixture.Caller.Store.Steps);
        Assert.Single(fixture.Cluster.First.Store.Issues);
        Assert.False(fixture.Cluster.State.Current!.OnboardingContracts!.OnlineGrants!.ContainsKey(grant.GrantId));
    }

    [Fact]
    public async Task LocalCommitFailureDoesNotSendGrantRequest()
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        fixture.Caller.Store.BeforeCommit = _ => throw new IOException("TestLocalCommitFailure");

        // Act
        Exception? failed = await Record.ExceptionAsync(() => fixture.Caller.Router.SignAsync(fixture.Grant(), token));

        // Assert
        Assert.IsType<IOException>(failed);
        Assert.Empty(fixture.Cluster.Sent);
        Assert.Single(fixture.Cluster.First.Store.Issues);
    }
}
