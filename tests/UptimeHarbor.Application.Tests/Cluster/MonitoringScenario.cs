using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MonitoringScenario : RevisionScenario
{
    public Guid A => Node;
    public Guid B { get; } = Guid.NewGuid();
    public Guid C { get; } = Guid.NewGuid();
    public Guid TargetId { get; }
    public Guid ChatId { get; } = Guid.NewGuid();
    public DateTimeOffset Start { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public List<RevisionCommand> Journal { get; } = [];

    public MonitoringScenario()
    {
        State = State with { Monitoring = new MonitoringImage
        {
            Nodes = ImmutableDictionary<Guid, Guid>.Empty.Add(A, Guid.NewGuid()).Add(B, Guid.NewGuid()).Add(C, Guid.NewGuid())
        } };
        RevisionCommand server = Server() with { CreatedAtUtc = Start };
        server = server with { Value = server.Value! with { ObserverNodes = [A, B] } };
        Apply(server);
        TargetId = server.EntityId;
        Apply(Command(ChatId, DeletedEntityKind.Chat, 0, new ConfigurationValue { Name = "chat", TelegramChatId = 123 }));
        Apply(Command(Guid.NewGuid(), DeletedEntityKind.Binding, 0, new ConfigurationValue
        {
            ParentId = TargetId, ChatId = ChatId, Generation = Guid.NewGuid(), Enabled = true
        }));
    }

    public MonitoringObservation Observation(Guid node, int seconds, bool success, string? name = null, long? sequence = null)
    {
        MonitoringTarget target = MonitoringTargets.Find(State, TargetId)!;
        Guid storage = State.Monitoring.Nodes[node];
        Guid stream = MonitoringKeys.Stream(TargetId, node, storage, target.CheckGeneration);
        long next = (State.Monitoring.Streams.GetValueOrDefault(stream)?.Sequence ?? 0) + 1;
        string? failure = "Timeout";
        if (success)
        {
            failure = null;
            name ??= "app";
        }

        return new(Guid.NewGuid(), TargetId, node, storage, target.CheckGeneration, target.ConfigRevision,
            sequence ?? next, Start.AddSeconds(seconds), 10, success, failure, name, null, true);
    }

    public CommandResult Observe(Guid node, int seconds, bool success, string? name = null)
    {
        MonitoringObservation observation = Observation(node, seconds, success, name);
        return Record(observation);
    }

    public CommandResult Record(params MonitoringObservation[] observations)
    {
        MonitoringObservation first = observations[0];
        RevisionCommand command = MonitoringCommands.Record(State, first.ObserverNodeId, first.StorageGeneration, observations.ToImmutableArray());
        return Accept(command);
    }

    public CommandResult Evaluate(int seconds, bool historical = false, long term = 1)
    {
        MonitoringTarget target = MonitoringTargets.Find(State, TargetId)!;
        RevisionCommand command = MonitoringCommands.Evaluate(State, target, A, State.Monitoring.Nodes[A],
            term, Start.AddSeconds(seconds), historical, Start.AddSeconds(seconds));
        return Accept(command, term);
    }

    public CommandResult Accept(RevisionCommand command, long term = 1)
    {
        Journal.Add(command);
        return Apply(command, Math.Max(term, State.Term));
    }

    public RevisionCommand Backlog(long term = 2)
    {
        return new RevisionCommand
        {
            ClusterId = State.ClusterId, ClusterGeneration = State.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = A, ActorUserId = Guid.Empty, ActorRevision = 0, EntityId = A, EntityKind = DeletedEntityKind.Node,
            Action = "PlanBacklogDelivery", BaseRevision = 0, CreatedAtUtc = Start.AddYears(2), SystemSource = "MonitoringCoordinator",
            Monitoring = new MonitoringCommand { StorageGeneration = State.Monitoring.Nodes[A], CoordinatorTerm = term, CutIndex = State.Index }
        };
    }

    public void ConfirmProblem(Guid chatId)
    {
        MonitoringIntent intent = State.Monitoring.Intents.Values.First(item => item.ChatId == chatId && item.Kind == DeliveryKind.Problem);
        MonitoringDeliveryEvidence evidence = new(intent.Id, Guid.NewGuid(), State.Index, State.Term,
            1, A, Guid.NewGuid(), Start.AddSeconds(180), Start.AddSeconds(181), 12345);
        MonitoringRecipients.AcceptedAttempt(State, evidence);
        State = State with { Monitoring = State.Monitoring with
        {
            ConfirmedDeliveries = State.Monitoring.ConfirmedDeliveries.Add(intent.Id, evidence),
            Intents = State.Monitoring.Intents.SetItem(intent.Id, intent with { State = DeliveryState.Succeeded, CompletedAtUtc = evidence.SucceededAtUtc })
        } };
    }
}
