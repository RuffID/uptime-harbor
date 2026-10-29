namespace UptimeHarbor.Domain.Cluster;

public class DeliveryIntent : ProjectionRecord
{
    private DeliveryIntent()
    {
    }

    public DeliveryIntent(Guid id, JournalPosition position, Guid? incidentId, Guid? eventId, Guid chatId,
        DeliveryKind kind, Guid bindingGeneration, string logicalKey, string protectedPayload,
        Guid? actorUserId = null) : base(id, position)
    {
        StorageGuards.Id(chatId, nameof(chatId));
        StorageGuards.Id(bindingGeneration, nameof(bindingGeneration));
        bool standalone = kind is DeliveryKind.Test or DeliveryKind.Summary;
        bool validEvent = incidentId.HasValue && eventId.HasValue;
        if (!Enum.IsDefined(kind) || actorUserId == Guid.Empty || incidentId == Guid.Empty || eventId == Guid.Empty
            || (kind == DeliveryKind.Test) != actorUserId.HasValue || standalone == validEvent
            || incidentId.HasValue != eventId.HasValue)
        {
            throw new ArgumentException("Invalid delivery kind or actor.");
        }

        IncidentId = incidentId;
        EventId = eventId;
        ChatId = chatId;
        Kind = kind;
        BindingGeneration = bindingGeneration;
        LogicalKey = StorageGuards.Text(logicalKey, 256, nameof(logicalKey));
        ProtectedPayload = StorageGuards.Payload(protectedPayload);
        ActorUserId = actorUserId;
    }

    public Guid? IncidentId { get; private set; }
    public Guid? EventId { get; private set; }
    public Guid ChatId { get; private set; }
    public DeliveryKind Kind { get; private set; }
    public Guid BindingGeneration { get; private set; }
    public string LogicalKey { get; private set; } = string.Empty;
    public string ProtectedPayload { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public DeliveryState State { get; private set; }
    public int AttemptCount { get; private set; }
    public Guid? ActiveAttemptId { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public Guid? SummaryId { get; private set; }

    public void BeginAttempt(Guid attemptId)
    {
        StorageGuards.Id(attemptId, nameof(attemptId));
        if (State is not (DeliveryState.Planned or DeliveryState.RetryDue) || AttemptCount >= 5)
        {
            throw new InvalidOperationException("Delivery cannot start another attempt.");
        }

        State = DeliveryState.AttemptStarted;
        ActiveAttemptId = attemptId;
        AttemptCount++;
        NextAttemptAtUtc = null;
    }

    /// <summary>Восстанавливает проекцию из полной истории реальных принятых попыток.</summary>
    public void RestoreAttempts(IReadOnlyList<DeliveryAttempt> history, DeliveryState state,
        DateTimeOffset? nextAttemptAtUtc, DateTimeOffset? completedAtUtc, Guid? summaryId)
    {
        if (State != DeliveryState.Planned || AttemptCount != 0 || history.Count is < 1 or > 5)
        {
            throw new InvalidOperationException("Invalid delivery restoration.");
        }

        DeliveryAttempt[] ordered = history.OrderBy(item => item.Number).ToArray();
        if (ordered.Any(item => item.IntentId != Id) || !ordered.Select(item => item.Number).SequenceEqual(Enumerable.Range(1, ordered.Length)))
        {
            throw new InvalidOperationException("Incomplete accepted attempt history.");
        }

        DeliveryAttempt latest = ordered[^1];
        bool succeeded = ordered.Any(item => item.Outcome == AttemptOutcome.Succeeded);
        bool terminal = state is DeliveryState.Succeeded or DeliveryState.Failed or DeliveryState.Cancelled;
        if (!Enum.IsDefined(state) || succeeded != (state == DeliveryState.Succeeded)
            || terminal != completedAtUtc.HasValue || (state == DeliveryState.RetryDue) != nextAttemptAtUtc.HasValue
            || state == DeliveryState.AttemptStarted && latest.Outcome != AttemptOutcome.Unknown
            || state != DeliveryState.Succeeded && state != DeliveryState.AttemptStarted && latest.Outcome == AttemptOutcome.Unknown
            || (state == DeliveryState.SuppressedBySummary) != summaryId.HasValue)
        {
            throw new InvalidOperationException("Accepted delivery state does not match attempts.");
        }

        if (completedAtUtc.HasValue)
        {
            StorageGuards.Utc(completedAtUtc.Value);
        }

        if (nextAttemptAtUtc.HasValue)
        {
            StorageGuards.Utc(nextAttemptAtUtc.Value);
        }

        State = state;
        AttemptCount = ordered.Length;
        ActiveAttemptId = latest.Id;
        NextAttemptAtUtc = nextAttemptAtUtc;
        CompletedAtUtc = completedAtUtc;
        SummaryId = summaryId;
    }

    public void RecordResult(Guid attemptId, bool succeeded, DateTimeOffset completedAtUtc,
        DateTimeOffset? nextAttemptAtUtc)
    {
        StorageGuards.Utc(completedAtUtc);
        if (nextAttemptAtUtc.HasValue)
        {
            StorageGuards.Utc(nextAttemptAtUtc.Value);
        }

        if (State == DeliveryState.Succeeded)
        {
            return;
        }

        if (ActiveAttemptId != attemptId || State != DeliveryState.AttemptStarted)
        {
            throw new InvalidOperationException("Result requires the accepted attempt.");
        }

        if (succeeded)
        {
            State = DeliveryState.Succeeded;
            CompletedAtUtc = completedAtUtc;
        }
        else if (AttemptCount < 5 && nextAttemptAtUtc > completedAtUtc)
        {
            State = DeliveryState.RetryDue;
            NextAttemptAtUtc = nextAttemptAtUtc;
        }
        else
        {
            State = DeliveryState.Failed;
            CompletedAtUtc = completedAtUtc;
        }
    }

    public void CancelNotStarted(DateTimeOffset cancelledAtUtc)
    {
        StorageGuards.Utc(cancelledAtUtc);
        if (State is DeliveryState.Planned or DeliveryState.RetryDue)
        {
            State = DeliveryState.Cancelled;
            CompletedAtUtc = cancelledAtUtc;
            NextAttemptAtUtc = null;
        }
    }

    public void AcceptLateSuccess(DeliveryAttempt acceptedAttempt)
    {
        ArgumentNullException.ThrowIfNull(acceptedAttempt);
        if (acceptedAttempt.IntentId != Id || acceptedAttempt.Number > AttemptCount
            || acceptedAttempt.Outcome != AttemptOutcome.Succeeded || !acceptedAttempt.CompletedAtUtc.HasValue)
        {
            throw new InvalidOperationException("Late success requires evidence of an accepted attempt for this intent.");
        }

        State = DeliveryState.Succeeded;
        CompletedAtUtc ??= acceptedAttempt.CompletedAtUtc;
        NextAttemptAtUtc = null;
    }

    public void SuppressBySummary(Guid summaryId)
    {
        StorageGuards.Id(summaryId, nameof(summaryId));
        if (State != DeliveryState.Planned && State != DeliveryState.RetryDue)
        {
            throw new InvalidOperationException("Only an unstarted delivery can enter a summary.");
        }

        State = DeliveryState.SuppressedBySummary;
        SummaryId = summaryId;
        NextAttemptAtUtc = null;
    }
}
