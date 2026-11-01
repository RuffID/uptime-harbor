using System.Collections.Immutable;
using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class AdministrationQueryTests
{
    [Fact]
    public async Task EveryAcceptedAdministratorReadsSameResources_UnknownOrDisabledActorDoesNoPendingIo()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest request = fixture.Server();
        await fixture.Commands().SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(request.OperationId);

        // Act
        AdministrationSnapshotDto first = fixture.Query().Read(fixture.Scenario.Node).Data!;
        AdministrationSnapshotDto second = fixture.Query(fixture.Scenario.OtherActor).Read(Guid.NewGuid()).Data!;
        ClusterPendingQuery unknown = new(fixture.Access(Guid.NewGuid()), fixture.State, fixture.Work, fixture.Payloads);
        ServiceResult<IReadOnlyList<AdministrationPendingDto>> denied = await unknown.ReadAsync(0, TestContext.Current.CancellationToken);
        AcceptedValue actor = fixture.State.Current!.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.State.Current with { Values = fixture.State.Current.Values.SetItem(actor.Id, actor with { Value = actor.Value with { Enabled = false } }) });
        ServiceResult<IReadOnlyList<AdministrationPendingDto>> disabled = await fixture.Pending().ReadAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(first.Entities, second.Entities);
        Assert.Contains(first.Entities, value => value.Id == request.EntityId);
        Assert.Equal(ServiceErrorType.Forbidden, denied.Error!.Type);
        Assert.Equal(ServiceErrorType.Forbidden, disabled.Error!.Type);
        Assert.Equal(0, fixture.Work.PageReads);
        Assert.Equal(ServiceErrorType.Forbidden, fixture.Query().Read(fixture.Scenario.Node).Error!.Type);
    }

    [Fact]
    public async Task ReadReauthorizesAfterStorageAwait_AndRecoveryClosesReadModels()
    {
        // Arrange
        AdministrationFixture fixture = new();
        fixture.Work.BeforePage = () => fixture.State.BeginRecovery();

        // Act
        ServiceResult<IReadOnlyList<AdministrationPendingDto>> result = await fixture.Pending().ReadAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, fixture.Work.PageReads);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
        Assert.Equal(ServiceErrorType.Forbidden, fixture.Query().Read(fixture.Scenario.Node).Error!.Type);
        Assert.Equal(ServiceErrorType.Forbidden, fixture.Pending().Find(Guid.NewGuid()).Error!.Type);
    }

    [Fact]
    public async Task AccountPendingAndConflictExposeOnlySafeSettings_AndKeepAcceptedWinner()
    {
        // Arrange
        AdministrationFixture fixture = new();
        AccountChangeRequest first = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "same", "One-secret-123!");
        AccountChangeRequest second = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "same", "Other-secret-123!");
        await fixture.Service.SubmitAsync(first, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.SubmitAsync(second, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(first.OperationId).Outcome);
        Assert.Equal(OperationOutcome.Conflict, fixture.Commit(second.OperationId).Outcome);

        // Act
        AdministrationSnapshotDto snapshot = fixture.Query().Read(fixture.Scenario.Node).Data!;
        IReadOnlyList<AdministrationPendingDto> pending = (await fixture.Pending().ReadAsync(0, TestContext.Current.CancellationToken)).Data!;
        string json = JsonSerializer.Serialize(new { snapshot, pending }, JsonSerializerOptions.Web);

        // Assert
        Assert.Single(snapshot.Conflicts);
        Assert.Equal("same", snapshot.Conflicts[0].Proposed!.Name);
        Assert.Contains(snapshot.Entities, account => account.Id == first.UserId);
        Assert.DoesNotContain(snapshot.Entities, account => account.Id == second.UserId);
        foreach (string secret in new[] { "password", "PasswordHash", "securityStamp", "concurrencyStamp", "protectedPayload", "proposedPayload", "prepared-", "One-secret", "Other-secret" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MetadataAndAvailabilityStayPerObserver_StaleCoverageDoesNotBecomeOutage()
    {
        // Arrange
        MonitoringScenario scenario = new();
        MonitoringObservation a = scenario.Observation(scenario.A, 10, true, "name-A") with { StartedAtUtc = scenario.Start };
        MonitoringObservation b = scenario.Observation(scenario.B, 11, true, "name-B") with { StartedAtUtc = scenario.Start.AddSeconds(5) };
        Assert.Equal(OperationOutcome.Committed, scenario.Record(a).Outcome);
        Assert.Equal(OperationOutcome.Committed, scenario.Record(b).Outcome);
        for (int second = 20; second <= 22; second++) Assert.Equal(OperationOutcome.Committed, scenario.Observe(scenario.B, second, false).Outcome);
        Assert.Equal(OperationOutcome.Committed, scenario.Evaluate(30).Outcome);

        // Act
        AdministrationTargetDto current = AdministrationMonitoring.Read(scenario.State, scenario.TargetId, scenario.Start.AddSeconds(30));
        AdministrationTargetDto stale = AdministrationMonitoring.Read(scenario.State, scenario.TargetId, scenario.Start.AddSeconds(300));

        // Assert
        Assert.Equal("Partial", current.Availability);
        Assert.Equal("Complete", current.Coverage);
        Assert.Equal("name-A", Assert.Single(current.Observers, observer => observer.NodeId == scenario.A).EndpointName);
        Assert.Equal("name-B", Assert.Single(current.Observers, observer => observer.NodeId == scenario.B).EndpointName);
        Assert.Equal(30, current.Observers.Single(observer => observer.NodeId == scenario.A).UptimeSeconds);
        Assert.Equal(25, current.Observers.Single(observer => observer.NodeId == scenario.B).UptimeSeconds);
        Assert.Equal("Unknown", stale.Availability);
        Assert.Equal("Lost", stale.Coverage);
        Assert.True(stale.EvaluationStale);
        Assert.All(stale.Observers, observer => Assert.Equal("Stale", observer.Status));
    }

    [Fact]
    public async Task ObserversAreChangedOnlyAfterAcceptance_AndNodeRoutesRemainIndependent()
    {
        // Arrange
        AdministrationFixture fixture = new();
        Guid otherNode = Guid.NewGuid();
        fixture.State.Publish(fixture.State.Current! with { Monitoring = new MonitoringImage
        {
            Nodes = ImmutableDictionary<Guid, Guid>.Empty.Add(fixture.Scenario.Node, Guid.NewGuid()).Add(otherNode, Guid.NewGuid())
        } });
        ConfigurationChangeRequest server = fixture.Server() with { Settings = fixture.Server().Settings! with { ObserverNodes = [fixture.Scenario.Node, otherNode] } };
        await fixture.Commands().SubmitAsync(server, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(server.OperationId).Outcome);
        ConfigurationChangeRequest changed = server with { OperationId = Guid.NewGuid(), BaseRevision = 3,
            Settings = server.Settings! with { ObserverNodes = [otherNode] } };

        // Act
        Assert.Equal("LocalPending", (await fixture.Commands().SubmitAsync(changed, fixture.Scenario.Node, TestContext.Current.CancellationToken)).Data!.Status);

        // Assert
        Assert.Equal(2, fixture.Query().Read(fixture.Scenario.Node).Data!.Entities.Single(entity => entity.Id == server.EntityId).Settings.ObserverNodes.Count);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(changed.OperationId).Outcome);
        Assert.Equal([otherNode], fixture.State.Current!.Values[server.EntityId].Value.ObserverNodes);

        // Arrange
        ConfigurationChangeRequest chat = new(Guid.NewGuid(), Guid.NewGuid(), "Chat", 0, "Upsert", new() { Name = "chat", TelegramChatId = 123 });
        await fixture.Commands().SubmitAsync(chat, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(chat.OperationId).Outcome);
        ConfigurationChangeRequest firstRoute = new(Guid.NewGuid(), Guid.NewGuid(), "NodeChatRoute", 0, "Upsert", new() { ParentId = fixture.Scenario.Node, ChatId = chat.EntityId });
        ConfigurationChangeRequest secondRoute = firstRoute with { OperationId = Guid.NewGuid(), EntityId = Guid.NewGuid(), Settings = firstRoute.Settings! with { ParentId = otherNode } };

        // Act
        foreach (ConfigurationChangeRequest route in new[] { firstRoute, secondRoute })
        {
            await fixture.Commands().SubmitAsync(route, fixture.Scenario.Node, TestContext.Current.CancellationToken);
            Assert.Equal(OperationOutcome.Committed, fixture.Commit(route.OperationId).Outcome);
        }

        // Assert
        Assert.Equal(2, fixture.Query().Read(fixture.Scenario.Node).Data!.Entities.Count(entity => entity.Kind == "NodeChatRoute"));
    }
}
