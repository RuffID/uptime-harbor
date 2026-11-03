using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedBootstrapTests
{
    [Fact]
    public async Task GenesisAccountAndSeparateAcceptedActivationEnablePrecedeAtomicSetupRevocation()
    {
        // Arrange
        await using ManagedBootstrapFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        await fixture.StartQuarantineAsync(token);
        await fixture.Driver.TickAsync(token);

        // Assert
        Assert.Empty(fixture.Accepted.Current!.Operations.Nodes);
        Assert.False(fixture.Installation.CommonOpen);
        Assert.False(fixture.Store.Account.Disabled);

        // Act
        Assert.True(await fixture.AccountBootstrap.InitializeAsync(token));
        await fixture.Driver.TickAsync(token);
        await fixture.Driver.TickAsync(token);
        await fixture.Driver.TickAsync(token);

        // Assert
        Assert.Equal(InstallationPhase.AwaitingActivation, fixture.Store.Profile!.Phase);
        Assert.Single(fixture.Accepted.Current!.Values);
        Assert.NotEqual(fixture.Store.Account.Id, fixture.Account.ActorUserId);
        Assert.False(fixture.Installation.CommonOpen);
        Assert.False(fixture.Readiness.CanWork(fixture.Runtime));

        // Act
        await fixture.Driver.TickAsync(token);

        // Assert
        Assert.True(fixture.Accepted.Current.Operations.Nodes[fixture.Options.NodeId].Activated);
        Assert.False(fixture.Installation.CommonOpen);
        Assert.False(fixture.Store.Account.Disabled);

        // Act
        await fixture.Driver.TickAsync(token);

        // Assert
        Assert.Equal(InstallationPhase.ClusterReady, fixture.Store.Profile.Phase);
        Assert.True(fixture.Installation.CommonOpen);
        Assert.True(fixture.Store.Account.Disabled);
        Assert.Null(fixture.Store.Account.PasswordHash);
        Assert.True(fixture.Readiness.CanWork(fixture.Runtime));
        Assert.Equal("Unknown", fixture.Readiness.Get("Telegram").Status);
        Assert.True(fixture.Native.Barriers > 0);
        Assert.Empty(fixture.Accepted.Current.Operations.Changes);
        Assert.Null(fixture.Accepted.Current.Operations.Sender);
        OnboardingContractSnapshot.Validate(fixture.Accepted.Current);
    }

    [Fact]
    public async Task LostProcessResponseKeepsOperationAndMajorityFailureDoesNotOpenSetup()
    {
        // Arrange
        await using ManagedBootstrapFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.StartQuarantineAsync(token);
        await fixture.AccountBootstrap.InitializeAsync(token);
        await fixture.Driver.TickAsync(token);
        await fixture.Driver.TickAsync(token);
        fixture.Native.LoseResponse = true;

        // Act
        Exception? response = await Record.ExceptionAsync(() => fixture.Driver.TickAsync(token));
        AcceptedOnlineGrant process = Assert.Single(fixture.Accepted.Current!.OnboardingContracts!.OnlineGrants!).Value;
        fixture.Native.Majority = false;
        Exception? majority = await Record.ExceptionAsync(() => fixture.Driver.TickAsync(token));

        // Assert
        Assert.IsType<HttpRequestException>(response);
        Assert.IsType<InvalidOperationException>(majority);
        Assert.Equal(OnlineGrantPurpose.Process, process.Grant.Purpose);
        Assert.False(fixture.Installation.CommonOpen);
        Assert.False(fixture.Store.Account.Disabled);

        // Act
        fixture.Native.Majority = true;
        await fixture.Driver.TickAsync(token);
        await fixture.Driver.TickAsync(token);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(1);
        await fixture.Driver.TickAsync(token);

        // Assert
        Assert.True(fixture.Installation.CommonOpen);
        Assert.Equal(process, fixture.Accepted.Current!.OnboardingContracts!.OnlineGrants![process.Grant.GrantId]);
        Assert.Single(fixture.Accepted.Current.OnboardingContracts.OnlineGrants.Values, item => item.ProcessRenewal);
        Assert.True(fixture.Enable.Allows());
    }

    [Fact]
    public async Task NativeGenesisPermitRejectsChangedOperationAndUnrelatedCommandWhileWorkIsClosed()
    {
        // Arrange
        await using ManagedBootstrapFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.StartQuarantineAsync(token);
        RevisionCommand changed = fixture.Account with { OperationId = Guid.NewGuid() };
        RevisionCommand unrelated = fixture.Account with { OperationId = Guid.NewGuid(), Action = "Upsert" };

        // Act
        Exception? changedId = await Record.ExceptionAsync(async () =>
            await fixture.Writer.ReplicateAsync(RevisionCodec.Encode(changed), RevisionCodec.COMMAND_ID, token));
        Exception? arbitrary = await Record.ExceptionAsync(async () =>
            await fixture.Writer.ReplicateAsync(RevisionCodec.Encode(unrelated), RevisionCodec.COMMAND_ID, token));

        // Assert
        Assert.IsType<InvalidOperationException>(changedId);
        Assert.IsType<InvalidOperationException>(arbitrary);
        Assert.Empty(fixture.Accepted.Current!.Results);
        Assert.False(fixture.Readiness.CanWork(fixture.Runtime));

        // Act
        bool accepted = await fixture.AccountBootstrap.InitializeAsync(token);

        // Assert
        Assert.True(accepted);
        Assert.False(fixture.Permit.Allows(fixture.Account, fixture.Accepted.Current!));
        Assert.False(fixture.Readiness.CanWork(fixture.Runtime));
    }
}
