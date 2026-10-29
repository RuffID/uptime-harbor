using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ClusterTestDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowerQueuesDurableUserCommandAndLeaderReauthorizesIt(bool disableBeforeAccept)
    {
        // Arrange
        using DeliveryHarness leader = new();
        await leader.PrepareAsync();
        RevisionStateStore follower = new();
        follower.Publish(leader.Image);
        ClusterOptions options = new() { Mode = "Restart", NodeId = Guid.NewGuid() };
        PendingMemory work = new();
        PendingPayloadProtection protection = new();
        PendingAuditRecorder audit = new();
        User user = new(leader.Actor);
        PendingCommandService pending = new(work, user, new AcceptedAdministratorAccessPolicy(user, follower),
            follower, protection, audit, new PendingAdmissionGate());
        ClusterTestDelivery submit = new(pending, follower, Options.Create(options), leader.Source.Time);
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IPendingControlUnitOfWork>(work).AddSingleton<IPendingOperationRepository>(work)
            .AddSingleton<IRevisionPayloadProtection>(protection).BuildServiceProvider();
        PendingReplicationPump pump = new(services.GetRequiredService<IServiceScopeFactory>(), leader, follower,
            new Follower(leader.Source.Accepted), Options.Create(options), leader.Source.Time, audit);

        // Act
        var submitted = await submit.SubmitAsync(leader.ChatId, TestContext.Current.CancellationToken);
        PendingOperation local = Assert.Single(work.Items.Values);

        // Assert
        Assert.True(submitted.Success);
        Assert.Equal(PendingState.LocalPending, local.State);
        Assert.Equal(options.NodeId, local.OriginNodeId);
        Assert.Equal(leader.Actor, local.ActorUserId);
        Assert.False(leader.Image.Monitoring.Intents.ContainsKey(local.Id));
        Assert.Empty(leader.Sent);

        // Arrange
        if (disableBeforeAccept)
        {
            AcceptedValue actor = leader.Image.Values[leader.Actor];
            Guid other = leader.Image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Account && item.Id != actor.Id).Id;
            RevisionCommand disable = leader.Command(actor.Id, actor.Kind, actor.Value with { Enabled = false })
                with { ActorUserId = other, ActorRevision = leader.Image.Values[other].Revision };
            await leader.Dispatcher.AcceptAsync(disable, leader.Source.Options.NodeId, TestContext.Current.CancellationToken);
        }

        // Act
        await pump.RunOnceAsync(TestContext.Current.CancellationToken);
        await leader.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PendingState.Resolved, local.State);
        if (disableBeforeAccept)
        {
            Assert.Equal(OperationOutcome.Rejected, leader.Image.Results[local.Id].Outcome);
            Assert.False(leader.Image.Monitoring.Intents.ContainsKey(local.Id));
            Assert.Empty(leader.Sent);
        }
        else
        {
            Assert.Equal(DeliveryState.Succeeded, leader.Image.Monitoring.Intents[local.Id].State);
            Assert.Equal(2, leader.Sent.Count);
            Assert.All(leader.Image.Monitoring.Attempts.Values,
                attempt => Assert.Equal(leader.Source.Options.NodeId, attempt.Authority.LeaderNodeId));
        }
    }

    private class User(Guid id) : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator => false;
        public bool IsEnabled => false;
        public Guid UserId => id;
    }

    private class Follower(AcceptedClusterState accepted) : IClusterStateView
    {
        public ConsensusStatus? Consensus => null;
        public AcceptedClusterState Accepted => accepted;
    }
}
