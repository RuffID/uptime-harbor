using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Web.Hubs;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class AccountSessionTests
{
    [Fact]
    public async Task VerificationStartedBeforeNewInvalidationCannotReopenAccounts()
    {
        // Arrange
        Fixture fixture = new();
        Caller actor = fixture.Connect(fixture.Actor);

        // Act
        bool confirmed = await fixture.Engine.ConfirmRecoveryAsync(_ =>
        {
            fixture.State.Invalidate();
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(confirmed);
        Assert.False(fixture.State.Available);
        Assert.True(actor.Aborted);
        Assert.False(fixture.Sessions.IsValid(actor.User));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("stamp")]
    [InlineData("disable")]
    [InlineData("delete")]
    public async Task AppliedAccountChangeAbortsOnlyAffectedProtectedSubscriptions(string change)
    {
        // Arrange
        Fixture fixture = new();
        Caller actor = fixture.Connect(fixture.Actor);
        Caller subject = fixture.Connect(fixture.Subject);
        ConfigurationValue value = fixture.State.Current!.Values[fixture.Subject].Value;
        string action = "Upsert";
        if (change == "password")
        {
            value = value with { PasswordHash = "new-protected-hash", SecurityStamp = "new-stamp" };
        }
        else if (change == "stamp")
        {
            value = value with { SecurityStamp = "new-stamp" };
        }
        else if (change == "disable")
        {
            value = value with { Enabled = false };
        }
        else
        {
            action = "Delete";
        }

        RevisionCommand command = fixture.Command(value) with { Action = action };
        if (action == "Delete")
        {
            command = command with { Value = null };
        }

        // Act
        await fixture.Engine.ApplyAsync(command, 3, 1, fixture.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(subject.Aborted);
        Assert.False(actor.Aborted);
        Assert.False(fixture.Sessions.IsValid(subject.User));
        Assert.Equal([actor.ConnectionId], fixture.Connections.AuthorizedIds());
        Assert.Equal(OperationOutcome.Committed, fixture.State.Current!.Results[command.OperationId].Outcome);
    }

    [Fact]
    public async Task PendingAndOrdinaryOfflineKeepSessionButApplyFailureAndRecoveryRevokeIt()
    {
        // Arrange
        Fixture fixture = new();
        Caller actor = fixture.Connect(fixture.Actor);
        RevisionCommand pending = fixture.Command(fixture.State.Current!.Values[fixture.Subject].Value with { Enabled = false });

        // Act
        IReadOnlyList<string> beforeApply = fixture.Connections.AuthorizedIds();
        bool normalRecheck = await fixture.Engine.ConfirmRecoveryAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(normalRecheck);
        Assert.False(actor.Aborted);
        Assert.Equal([actor.ConnectionId], beforeApply);
        Assert.True(fixture.Sessions.IsValid(actor.User));

        // Act
        fixture.Projection.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ApplyAsync(pending, 3, 1, fixture.Node, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(actor.Aborted);
        Assert.False(fixture.Sessions.IsValid(actor.User));
        Assert.False(fixture.State.Available);
        Assert.False(fixture.State.Current!.Results.ContainsKey(pending.OperationId));

        // Act
        fixture.State.Publish(fixture.State.Current!);

        // Assert
        Assert.False(fixture.State.Available);
        Assert.False(fixture.Sessions.IsValid(actor.User));

        // Act
        fixture.State.ConfirmRecovery(fixture.State.Current!, fixture.State.RecoveryVersion);

        // Assert
        Assert.False(fixture.Sessions.IsValid(actor.User));
        Assert.Empty(fixture.Connections.AuthorizedIds());
        Assert.False(fixture.Connect(fixture.Actor).Aborted);
    }

    [Fact]
    public void ClaimsAloneAndCookieFromAnotherNodeCannotSubscribe()
    {
        // Arrange
        Fixture fixture = new();
        Caller valid = fixture.Connect(fixture.Actor);
        AcceptedAccountSession otherNode = new(fixture.State);
        ClaimsPrincipal foreign = otherNode.Create(RevisionAccounts.Materialize(fixture.State.Current!.Values[fixture.Actor]), "cookie");
        Caller copied = new(foreign);
        Caller forged = new(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, fixture.Actor.ToString()), new Claim(ClaimTypes.Role, "Administrator")], "cookie")));

        // Act
        bool foreignAllowed = fixture.Connections.Register(copied);
        bool claimsAllowed = fixture.Connections.Register(forged);

        // Assert
        Assert.False(foreignAllowed);
        Assert.False(claimsAllowed);
        Assert.True(copied.Aborted);
        Assert.True(forged.Aborted);
        Assert.False(valid.Aborted);
        Assert.Equal([valid.ConnectionId], fixture.Connections.AuthorizedIds());
    }

    private class Fixture
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid Subject { get; } = Guid.NewGuid();
        public Guid Node { get; } = Guid.NewGuid();
        public RevisionStateStore State { get; } = new();
        public AcceptedAccountSession Sessions { get; }
        public MonitoringConnections Connections { get; }
        public Projection Projection { get; } = new();
        public RevisionApplyEngine Engine { get; }

        public Fixture()
        {
            Guid role = Guid.NewGuid();
            ConfigurationValue credential = new() { Name = "actor", RoleId = role,
                PasswordHash = "protected-hash", SecurityStamp = "stamp", ConcurrencyStamp = "concurrency" };
            RevisionImage image = new(Guid.NewGuid(), 1) { Index = 2, Term = 1 };
            image = image with { Values = image.Values.Add(Actor, new AcceptedValue(Actor, DeletedEntityKind.Account, 1, credential))
                .Add(Subject, new AcceptedValue(Subject, DeletedEntityKind.Account, 2, credential with { Name = "subject" })) };
            State.Publish(image);
            Sessions = new AcceptedAccountSession(State);
            Connections = new MonitoringConnections(State, Sessions, NullLogger<MonitoringConnections>.Instance);
            Engine = new RevisionApplyEngine(State, new RevisionReducer(), Projection);
        }

        public Caller Connect(Guid userId)
        {
            Caller caller = new(Sessions.Create(RevisionAccounts.Materialize(State.Current!.Values[userId]), "cookie"));
            Connections.Register(caller);
            return caller;
        }

        public RevisionCommand Command(ConfigurationValue value)
        {
            return new RevisionCommand { ClusterId = State.Current!.ClusterId, ClusterGeneration = 1,
                OperationId = Guid.NewGuid(), OriginNodeId = Node, ActorUserId = Actor, ActorRevision = 1,
                EntityId = Subject, EntityKind = DeletedEntityKind.Account, Action = "Upsert", BaseRevision = 2,
                CreatedAtUtc = DateTimeOffset.UnixEpoch, Value = value };
        }
    }

    private class Caller(ClaimsPrincipal user) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString();
        public override string? UserIdentifier => user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } = new ConcurrentDictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort()
        {
            Aborted = true;
        }
    }

    private class Projection : IRevisionProjectionStore
    {
        public bool Fail { get; set; }
        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
        {
            if (Fail)
            {
                throw new IOException("StorageUnavailable");
            }

            return Task.CompletedTask;
        }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task InstallAsync(RevisionImage image, Guid snapshotId, string digest, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
