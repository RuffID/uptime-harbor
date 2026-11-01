using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

/// <summary>Production gate/consensus/dispatcher/reducer с изолированными transport и storage.</summary>
internal class DeliveryHarness : IDisposable, IRevisionTransport, IConsensusWriter, IAcceptedTelegramTransport, IDeliveryAudit
{
    public MonitoringHarness Source { get; } = new();
    public DeliveryMemory Local { get; } = new();
    public SenderAuthorityGate Gate { get; }
    public RevisionDispatcher Dispatcher { get; }
    public ServiceProvider Services { get; }
    public AcceptedDeliveryDispatcher Delivery { get; private set; }
    public Guid ChatId { get; } = Guid.NewGuid();
    public Guid Actor { get; }
    public RevisionImage Image => Source.State.Current!;
    public List<Guid> Sent { get; } = [];
    public List<(Guid Attempt, string Outcome)> Audit { get; } = [];
    public Func<RevisionCommand, Task>? AfterBegin { get; set; }
    public Func<NotificationRecipient, Task<long>>? Send { get; set; }
    public bool RejectResults { get; set; }
    public bool LoseResultResponse { get; set; }

    public DeliveryHarness()
    {
        Actor = Image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Account).Id;
        Source.Readiness.Report("Monitoring", true, "Ready", TimeSpan.FromHours(1));
        Source.Readiness.Report("Telegram", true, "Ready", TimeSpan.FromHours(1));
        Dispatcher = new(this, Source.State, Source, time: Source.Time, deliveryAudit: this);
        DeliverySenderConsensus consensus = new(Dispatcher, Source.State, Options.Create(Source.Options), Source.Time);
        Gate = new(Source, Source.Readiness, consensus, Source.Time);
        Gate.Tick();
        Services = new ServiceCollection().AddScoped<IDeliveryResultUnitOfWork>(_ => Local.CreateWork())
            .AddScoped<IAcceptedTelegramTransport>(_ => new Sender(this)).BuildServiceProvider();
        Delivery = CreateDispatcher();
    }

    public AcceptedDeliveryDispatcher CreateDispatcher()
    {
        return new(Source.State, Source, Source.Readiness, Gate, this, Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(Source.Options), Source.Time, NullLogger<AcceptedDeliveryDispatcher>.Instance);
    }

    public async Task PrepareAsync(bool test = true)
    {
        await ApplyAsync(Guid.NewGuid(), DeletedEntityKind.Account, Image.Values[Actor].Value with { Name = "other" });
        await ApplyAsync(ChatId, DeletedEntityKind.Chat, new ConfigurationValue { Name = "chat", TelegramChatId = 123 });
        if (test)
        {
            await Dispatcher.AcceptAsync(Command(ChatId, DeletedEntityKind.Chat, null) with { Action = "TestDelivery", BaseRevision = 0 },
                Source.Options.NodeId, CancellationToken.None);
        }
        else
        {
            Guid targetId = Image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server).Id;
            await ApplyAsync(Guid.NewGuid(), DeletedEntityKind.Binding, new ConfigurationValue
            {
                ParentId = targetId, ChatId = ChatId, Generation = Guid.NewGuid(), Enabled = true
            });
            MonitoringTarget target = MonitoringTargets.Find(Image, targetId)!;
            foreach (int number in Enumerable.Range(1, 3))
            {
                MonitoringObservation observation = new(Guid.NewGuid(), targetId, Source.Options.NodeId, Source.Options.StorageGeneration,
                    target.CheckGeneration, target.ConfigRevision, number, Source.Time.Now, 1, false, "Timeout", null, null, true);
                await Dispatcher.AcceptAsync(MonitoringCommands.Record(Image, Source.Options.NodeId, Source.Options.StorageGeneration,
                    [observation]), Source.Options.NodeId, CancellationToken.None);
                Source.Time.Now = Source.Time.Now.AddSeconds(1);
                Gate.Tick();
            }

            await Dispatcher.AcceptAsync(MonitoringCommands.Evaluate(Image, target, Source.Options.NodeId, Source.Options.StorageGeneration,
                1, Source.Time.Now, false, Source.Time.Now), Source.Options.NodeId, CancellationToken.None);
        }

        foreach (int _ in Enumerable.Range(1, 12))
        {
            Source.Time.Now = Source.Time.Now.AddSeconds(1);
            Gate.Tick();
        }

        Xunit.Assert.True(Delivery.ReconcileQueue());
        Xunit.Assert.True(await Gate.RenewAsync(CancellationToken.None));
    }

    public RevisionCommand Command(Guid id, DeletedEntityKind kind, ConfigurationValue? value)
    {
        return new RevisionCommand
        {
            ClusterId = Image.ClusterId, ClusterGeneration = Image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = Source.Options.NodeId, ActorUserId = Actor, ActorRevision = Image.Values[Actor].Revision,
            EntityId = id, EntityKind = kind, Value = value, BaseRevision = Image.Values.GetValueOrDefault(id)?.Revision ?? 0,
            Action = "Upsert", CreatedAtUtc = Source.Time.Now
        };
    }

    public async Task ApplyAsync(Guid id, DeletedEntityKind kind, ConfigurationValue value)
    {
        CommandResult result = await Source.Dispatcher.AcceptAsync(Command(id, kind, value), Source.Options.NodeId, CancellationToken.None);
        Xunit.Assert.Equal(OperationOutcome.Committed, result.Outcome);
    }

    public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken cancellationToken)
    {
        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        if (RejectResults && command.Action == "RecordDelivery")
        {
            throw new IOException("InjectedResultFailure");
        }

        long index = await Source.ReplicateAsync(payload, commandId, cancellationToken);
        if (command.Action == "BeginDelivery" && AfterBegin is not null)
        {
            await AfterBegin(command);
        }

        return index;
    }

    public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("A chat failure cannot cause an election.");
    }

    public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken cancellationToken)
    {
        List<CommandResult> results = [];
        foreach (RevisionCommand command in commands)
        {
            results.Add(await Dispatcher.AcceptAsync(command, command.OriginNodeId, cancellationToken));
        }

        if (LoseResultResponse && commands.Any(item => item.Action == "RecordDelivery"))
        {
            throw new IOException("InjectedResultResponseLoss");
        }

        return results;
    }

    public async Task<long> SendAcceptedAsync(NotificationRecipient recipient, string message, CancellationToken token)
    {
        lock (Sent)
        {
            Sent.Add(recipient.Id);
        }

        if (Send is not null)
        {
            return await Send(recipient);
        }

        return 123;
    }

    public void Accepted(Guid nodeId, Guid? incidentId, Guid deliveryId, Guid attemptId, long term, string outcome, string? category)
    {
        Audit.Add((attemptId, outcome));
    }

    public void Dispose()
    {
        Gate.Dispose();
        Services.Dispose();
        Source.Dispose();
    }

    private class Sender(DeliveryHarness owner) : IAcceptedTelegramTransport
    {
        public Task<long> SendAcceptedAsync(NotificationRecipient recipient, string message, CancellationToken token)
        {
            return owner.SendAcceptedAsync(recipient, message, token);
        }
    }
}
