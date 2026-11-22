using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ManualSenderTests
{
    [Fact]
    public async Task EngineerTransfersDeliveryAcrossLeaderChangesAndRejectsTokenlessVoter()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        CandidateHealth health = new();
        using DeliveryHarness log = new(health);
        using Node first = new(log, log.Source.Options.NodeId, log.Source.Options.StorageGeneration, true);
        using Node second = new(log, Guid.NewGuid(), Guid.NewGuid(), true);
        using Node third = new(log, Guid.NewGuid(), Guid.NewGuid(), false);
        Node[] nodes = [first, second, third];
        foreach (Node node in nodes)
        {
            health.Nodes.Add(node.Id, node);
            await node.Probe.ProbeAsync(token);
        }

        RevisionImage image = log.Image;
        ClusterOperationsImage operations = new() { MembershipVersion = 1, BotId = 123 };
        MonitoringImage monitoring = image.Monitoring;
        foreach (Node node in nodes)
        {
            OperationalNode member = new(node.Id, node.Options.StorageGeneration, $"https://{node.Id:N}.invalid/internal/raft",
                node.Id.ToString(), true, true, false, 1);
            operations = operations with { Nodes = operations.Nodes.Add(member.NodeId, member) };
            monitoring = monitoring with { Nodes = monitoring.Nodes.SetItem(member.NodeId, member.StorageGeneration) };
        }

        log.Source.State.Publish(image with { Operations = operations, Monitoring = monitoring });
        log.Source.State.Changed += () =>
        {
            log.Source.Accepted = log.Source.Accepted with { AssignedSenderNodeId = log.Image.Operations.AssignedSenderNodeId,
                SenderEpoch = log.Image.Operations.Sender?.SenderEpoch ?? Guid.Empty };
        };
        Elect(first, 1);
        await log.ApplyAsync(log.ChatId, DeletedEntityKind.Chat, new ConfigurationValue { Name = "test", TelegramChatId = 123 });

        // Act
        await AssignAsync(log, first, first, token);
        await QueueEventAsync(first);
        await DispatchAsync(nodes);

        // Assert
        Assert.Equal(1, first.Sends);
        Assert.Equal(0, second.Sends);

        // Act — №1 недоступен, большинство и очередь остаются на №2 и №3.
        Elect(third, 2);
        await ReopenAsync(third);
        Guid queued = await QueueEventAsync(third);
        await DispatchAsync([second, third]);

        // Assert
        Assert.Equal(first.Id, log.Image.Operations.AssignedSenderNodeId);
        Assert.Equal(DeliveryState.Planned, log.Image.Monitoring.Intents[queued].State);
        Assert.Equal(0, second.Sends);
        Assert.Equal(0, third.Sends);

        // Act — заявка поступает через follower №2, журнал принимает лидер №3.
        await AssignAsync(log, second, second, token);
        await DispatchAsync([second, third]);
        await DispatchAsync([second, third]);

        // Assert
        Assert.Equal(1, second.Sends);
        Assert.Equal(second.Id, log.Image.Monitoring.ConfirmedDeliveries[queued].SenderNodeId);
        Assert.Single(log.Image.Monitoring.Attempts.Values, attempt => attempt.IntentId == queued);

        // Act — возврат и избрание №1 не меняют назначение. У №3 токена нет.
        Elect(first, 3);
        await ReopenAsync(first);
        SenderAuthorityKey? previous = log.Image.Operations.Sender;
        CommandResult rejected = await AssignAsync(log, first, third, token);
        Guid afterReturn = await QueueEventAsync(first);
        await DispatchAsync(nodes);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, rejected.Outcome);
        Assert.Equal("TelegramSenderUnavailable", rejected.Reason);
        Assert.Equal(previous, log.Image.Operations.Sender);
        Assert.Equal(second.Id, log.Image.Monitoring.ConfirmedDeliveries[afterReturn].SenderNodeId);
        Assert.Equal(1, first.Sends);
        Assert.Equal(2, second.Sends);

        // Act
        await AssignAsync(log, second, first, token);
        Guid afterTransfer = await QueueEventAsync(second);
        await DispatchAsync(nodes);

        // Assert
        Assert.Equal(first.Id, log.Image.Monitoring.ConfirmedDeliveries[afterTransfer].SenderNodeId);
        Assert.Equal(2, first.Sends);
        Assert.Equal(2, second.Sends);
        Assert.Equal(0, third.Sends);
        Assert.Equal(4, log.Image.Monitoring.Attempts.Count);

        void Elect(Node leader, long term)
        {
            log.Source.Consensus = log.Source.Consensus! with { NodeId = leader.Id, LeaderNodeId = leader.Id,
                Term = term, VotingNodes = health.Nodes.Keys.ToHashSet() };
        }

        async Task ReopenAsync(Node leader)
        {
            RevisionCommand epoch = Assert.IsType<RevisionCommand>(ClusterLifecycleCommands.OpenSenderEpoch(log.Image,
                log.Source.Consensus!, leader.Options.StorageGeneration, log.Source.Time.Now));
            CommandResult result = await log.Dispatcher.AcceptAsync(epoch, leader.Id, token);
            Assert.Equal(OperationOutcome.Committed, result.Outcome);
        }

        async Task<Guid> QueueEventAsync(Node origin)
        {
            RevisionCommand command = log.Command(log.ChatId, DeletedEntityKind.Chat, null) with
            {
                OriginNodeId = origin.Id, Action = "TestDelivery", BaseRevision = 0
            };
            CommandResult result = await log.Dispatcher.AcceptAsync(command, origin.Id, token);
            Assert.Equal(OperationOutcome.Committed, result.Outcome);
            return command.OperationId;
        }

        async Task DispatchAsync(Node[] online)
        {
            foreach (int _ in Enumerable.Range(0, 14))
            {
                log.Source.Time.Now = log.Source.Time.Now.AddSeconds(1);
                foreach (Node node in online)
                {
                    node.Gate.ConfirmMembershipReconciled();
                    node.Gate.Tick();
                    node.Readiness.Report("Clock", true, "Ready", TimeSpan.FromHours(1));
                }
            }

            foreach (Node node in online)
            {
                await node.Gate.RenewAsync(token);
                await node.Delivery.RunOnceAsync(token);
            }
        }
    }

    private static async Task<CommandResult> AssignAsync(DeliveryHarness log, Node origin, Node target, CancellationToken token)
    {
        User user = new(log.Actor);
        PendingMemory memory = new();
        PendingPayloadProtection protection = new();
        PendingAuditRecorder audit = new();
        AcceptedAdministratorAccessPolicy policy = new(user, log.Source.State);
        PendingCommandService pending = new(memory, user, policy, log.Source.State, protection, audit, new());
        ClusterOperationsAdministration admin = new(new(policy, user, log.Source.State), log.Source.State,
            pending, memory, protection, Options.Create(origin.Options), log.Source.Time, user);
        ClusterOperationRequest request = new(Guid.NewGuid(), "AssignSender", target.Id, log.Image.Operations.Revision, 1);
        var submitted = await admin.SubmitAsync(request, token);
        Assert.True(submitted.Success);
        Assert.Equal("LocalPending", submitted.Data!.Status);
        using ServiceProvider services = new ServiceCollection().AddSingleton<IPendingControlUnitOfWork>(memory)
            .AddSingleton<IPendingOperationRepository>(memory).AddSingleton<IRevisionPayloadProtection>(protection).BuildServiceProvider();
        PendingReplicationPump pump = new(services.GetRequiredService<IServiceScopeFactory>(), log, log.Source.State,
            origin, Options.Create(origin.Options), log.Source.Time, audit);
        await pump.RunOnceAsync(token);
        return log.Image.Results[request.OperationId];
    }

    private class CandidateHealth : ISenderCandidateHealth
    {
        public Dictionary<Guid, Node> Nodes { get; } = [];

        public bool IsReady(Guid nodeId, ConsensusStatus leader)
        {
            return Nodes[nodeId].Readiness.Get("Telegram").Status == "Ready";
        }
    }

    private class User(Guid id) : IUserAccessContext
    {
        public Guid UserId => id;
        public bool IsAuthenticated => true;
        public bool IsAdministrator => true;
        public bool IsEnabled => true;
    }

    private class Node : IClusterStateView, IDisposable
    {
        private readonly DeliveryHarness log;
        private readonly ServiceProvider services;
        public Guid Id => Options.NodeId;
        public ClusterOptions Options { get; }
        public NodeReadiness Readiness { get; }
        public SenderAuthorityGate Gate { get; }
        public AcceptedDeliveryDispatcher Delivery { get; }
        private readonly Telegram telegram;
        public TelegramHealthProbe Probe { get; }
        public int Sends => telegram.Sends;
        public AcceptedClusterState Accepted => log.Source.Accepted;
        public ConsensusStatus? Consensus => log.Source.Consensus! with { NodeId = Id, IsLeader = log.Source.Consensus.NodeId == Id,
            LeadershipToken = new CancellationToken(log.Source.Consensus.NodeId != Id) };

        public Node(DeliveryHarness log, Guid id, Guid storage, bool hasToken)
        {
            this.log = log;
            Options = new() { Mode = "Restart", NodeId = id, StorageGeneration = storage, ExpectedBotId = 123 };
            Readiness = new(log.Source.Time);
            foreach (string component in new[] { "Monitoring", "Storage", "Clock", "Database" })
            {
                Readiness.Report(component, true, "Ready", TimeSpan.FromHours(1));
            }

            string botToken = string.Empty;
            if (hasToken)
            {
                botToken = "123:" + new string('x', 35);
            }

            IOptions<TelegramOptions> settings = Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = botToken });
            Assert.True(Readiness.ConfirmRecovery(0, Accepted, Consensus!));
            IOptions<ClusterOptions> configuration = Microsoft.Extensions.Options.Options.Create(Options);
            Gate = new(this, Readiness, new DeliverySenderConsensus(log, log.Source.State, configuration, log.Source.Time), log.Source.Time);
            DeliveryMemory local = new();
            telegram = new Telegram();
            Probe = new TelegramHealthProbe(telegram, settings, configuration, Readiness, log.Source.Time);
            services = new ServiceCollection().AddScoped<IDeliveryResultUnitOfWork>(_ => local.CreateWork())
                .AddSingleton<IAcceptedTelegramTransport>(new TelegramHttpNotificationSender(telegram.Client, settings)).BuildServiceProvider();
            Delivery = new(log.Source.State, this, Readiness, Gate, log, services.GetRequiredService<IServiceScopeFactory>(),
                configuration, log.Source.Time, NullLogger<AcceptedDeliveryDispatcher>.Instance);
        }

        public void Dispose()
        {
            Gate.Dispose();
            services.Dispose();
            telegram.Client.Dispose();
            telegram.Dispose();
        }
    }

    private class Telegram : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient Client { get; }
        public int Sends { get; private set; }

        public Telegram()
        {
            Client = new HttpClient(this, false) { BaseAddress = new Uri("https://telegram.invalid/") };
        }

        public HttpClient CreateClient(string name)
        {
            return Client;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/getMe", StringComparison.Ordinal))
            {
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, result = new { id = 123 } }) };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            JsonElement body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(123, body.GetProperty("chat_id").GetInt64());
            Assert.Contains("тестовое", body.GetProperty("text").GetString());
            Sends++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, result = new { message_id = Sends } }) };
        }
    }
}
