using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

internal class DeliveryScenario : MonitoringScenario
{
    public Guid Epoch { get; } = Guid.NewGuid();
    public DateTimeOffset Now { get; set; }

    public DeliveryScenario()
    {
        foreach (int second in new[] { 1, 60, 120 })
        {
            Observe(A, second, false);
            Observe(B, second, true, "source-b");
        }

        Evaluate(121);
        Now = Start.AddSeconds(122);
    }

    public MonitoringIntent Problem => State.Monitoring.Intents.Values.Single(item => item.Kind == DeliveryKind.Problem);

    public SenderAuthorityKey Key()
    {
        return new(State.ClusterId, State.Generation, 1, State.Term, A, Epoch);
    }

    public RevisionCommand Begin(MonitoringIntent? selected = null)
    {
        selected ??= Problem;
        int number = State.Monitoring.Attempts.Values.Count(item => item.IntentId == selected.Id) + 1;
        return DeliveryCommands.Create(State, A, State.Monitoring.Nodes[A], "BeginDelivery", selected.Id,
            Guid.NewGuid(), Now, new DeliveryCommand { Authority = Key(), AttemptId = DeliveryCommands.AttemptId(selected.Id, number) });
    }

    public RevisionCommand Result(Guid attemptId, AttemptOutcome outcome = AttemptOutcome.Succeeded, string? failure = null)
    {
        long? message = null;
        if (outcome == AttemptOutcome.Succeeded)
        {
            message = 123;
        }

        return DeliveryCommands.Create(State, A, State.Monitoring.Nodes[A], "RecordDelivery", State.Monitoring.Attempts[attemptId].IntentId,
            Guid.NewGuid(), Now, new DeliveryCommand { AttemptId = attemptId, Outcome = outcome,
                CompletedAtUtc = Now, TelegramMessageId = message, FailureKind = failure }, transport: true);
    }

    public Guid StartAttempt(MonitoringIntent? intent = null)
    {
        RevisionCommand command = Begin(intent);
        Assert.Equal(OperationOutcome.Committed, Accept(command, State.Term).Outcome);
        return command.Delivery!.AttemptId;
    }

    public CommandResult Remove(Guid id)
    {
        AcceptedValue cell = State.Values[id];
        return Accept(Command(id, cell.Kind, cell.Revision, null) with { Action = "Delete", CreatedAtUtc = Now }, State.Term);
    }

    public void Recover()
    {
        Observe(A, 180, true, "source-a");
        Observe(B, 180, true, "source-b");
        Evaluate(181, term: State.Term);
        Now = Start.AddSeconds(182);
    }
}
