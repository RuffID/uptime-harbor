using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class AdministrationHttpTests
{
    [Theory]
    [InlineData(true, true, 200)]
    [InlineData(false, true, 401)]
    [InlineData(true, false, 403)]
    public async Task ReadModelsAndMutationsRequireActiveAcceptedAdministrator(bool authenticated, bool enabled, int status)
    {
        // Arrange
        Fixture fixture = new(authenticated, enabled);

        // Act
        JsonResult read = fixture.Controller.Read();
        JsonResult local = await fixture.Controller.ReadPendingAsync(0, TestContext.Current.CancellationToken);
        JsonResult result = await fixture.Controller.SubmitAsync(new(Guid.NewGuid(), fixture.Chat, "Chat", 0, "TestDelivery"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(status, read.StatusCode);
        Assert.Equal(status, local.StatusCode);
        if (status == 200)
        {
            Assert.Equal(202, result.StatusCode);
            Assert.Single(fixture.Work.Items);
        }
        else
        {
            Assert.Equal(status, result.StatusCode);
            Assert.Empty(fixture.Work.Items);
            Assert.Equal(0, fixture.Work.Reads);
        }
    }

    [Fact]
    public async Task TestSend202MeansDurableQueue_NotTelegramSuccess_AndRetryUsesSameOperation()
    {
        // Arrange
        Fixture fixture = new(true, true);
        ConfigurationChangeRequest request = new(Guid.NewGuid(), fixture.Chat, "Chat", 0, "TestDelivery");

        // Act
        JsonResult first = await fixture.Controller.SubmitAsync(request, TestContext.Current.CancellationToken);
        JsonResult retry = await fixture.Controller.SubmitAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(202, first.StatusCode);
        Assert.Equal(202, retry.StatusCode);
        Assert.Single(fixture.Work.Items);
        Assert.Empty(fixture.State.Current!.Monitoring.Intents);
        Assert.Empty(fixture.State.Current.Monitoring.Attempts);
        Assert.Contains("LocalPending", System.Text.Json.JsonSerializer.Serialize(first.Value));
        RevisionCommand stored = RevisionCodec.Decode(Convert.FromBase64String(fixture.Work.Items[request.OperationId].ProtectedPayload));
        Assert.Equal("TestDelivery", stored.Action);
        Assert.Equal(fixture.Actor, stored.ActorUserId);
    }

    private class Fixture
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid Chat { get; } = Guid.NewGuid();
        public RevisionStateStore State { get; } = new();
        public MemoryPending Work { get; } = new();
        public ClusterAdministrationController Controller { get; }

        public Fixture(bool authenticated, bool enabled)
        {
            State.Publish(new(Guid.NewGuid(), 1) { Index = 2, Term = 1, Values = ImmutableDictionary<Guid, AcceptedValue>.Empty
                .Add(Actor, new(Actor, DeletedEntityKind.Account, 1, new() { Name = "admin", Enabled = enabled,
                    RoleId = Guid.NewGuid(), PasswordHash = "hash", SecurityStamp = "stamp", ConcurrencyStamp = "stamp" }))
                .Add(Chat, new(Chat, DeletedEntityKind.Chat, 2, new() { Name = "chat", TelegramChatId = 123 })) });
            User user = new(Actor, authenticated);
            AcceptedAdministratorAccessPolicy policy = new(user, State);
            ClusterAdministrationAccess access = new(policy, user, State);
            Protection protection = new();
            PendingCommandService pending = new(Work, user, policy, State, protection, new Audit(), new PendingAdmissionGate());
            ClusterConfigurationCommands commands = new(access, State, pending, Work, protection, TimeProvider.System);
            Controller = new(new(access, State, user, TimeProvider.System), new(access, State, Work, protection), commands,
                new ClusterBrowserDiagnostics(access, null!, null!, null!, null!, null!, null!, null!, null!),
                Options.Create(new ClusterOptions { Mode = "Restart", NodeId = Guid.NewGuid() }));
        }
    }

    private class User(Guid id, bool authenticated) : IUserAccessContext
    {
        public Guid UserId { get; } = id;
        public bool IsAuthenticated { get; } = authenticated;
        public bool IsEnabled { get; } = false;
        public bool IsAdministrator { get; } = false;
    }
    private class Protection : IRevisionPayloadProtection
    {
        public string Protect(ReadOnlySpan<byte> payload) { return Convert.ToBase64String(payload); }
        public byte[] Unprotect(string payload) { return Convert.FromBase64String(payload); }
    }
    private class Audit : IRevisionAudit
    {
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome) { }
    }
    private class MemoryPending : IPendingControlUnitOfWork, IPendingOperationRepository
    {
        public Dictionary<Guid, PendingOperation> Items { get; } = [];
        public int Reads { get; private set; }
        public IPendingOperationRepository PendingOperations { get { return this; } }
        public Task<PendingOperation?> GetAsync(Guid id, CancellationToken token = default) { Reads++; return Task.FromResult(Items.GetValueOrDefault(id)); }
        public void Create(PendingOperation operation) { Items.Add(operation.Id, operation); }
        public Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly, CancellationToken token = default)
        {
            Reads++; return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Skip(offset).Take(limit).ToArray());
        }
        public Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset now, int limit, CancellationToken token = default) { throw new NotSupportedException(); }
        public Task<PendingBacklog> GetBacklogAsync(CancellationToken token = default) { return Task.FromResult(new PendingBacklog(Items.Count, 0, null)); }
        public Task<int> SaveChangesAsync(CancellationToken token = default) { return Task.FromResult(1); }
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken token = default) { return action(token); }
    }
}
