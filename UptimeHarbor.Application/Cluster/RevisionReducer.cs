using System.Globalization;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Чистое последовательное решение принятой команды. Не выполняет I/O и не читает часы узла.</summary>
public class RevisionReducer
{
    public RevisionImage Apply(RevisionImage state, RevisionCommand command, long index, long term)
    {
        RevisionCodec.Validate(command);
        if (command.ClusterId != state.ClusterId || command.ClusterGeneration != state.Generation
            || index <= state.Index || term < state.Term)
        {
            throw new InvalidDataException("InvalidCommittedPrefix");
        }

        string digest = RevisionCodec.Digest(command);
        RevisionImage next = state with { Index = index, Term = term, Digest = digest };
        return Execute(next, command, digest);
    }

    private RevisionImage Execute(RevisionImage state, RevisionCommand command, string digest)
    {
        if (state.Results.TryGetValue(command.OperationId, out CommandResult? prior))
        {
            if (prior.Digest != digest)
            {
                throw new InvalidDataException("IdempotencyKeyReuse");
            }

            return state;
        }

        if (command.Action == "BootstrapAccount")
        {
            bool emptyAccounts = !state.Values.Values.Any(value => value.Kind == DeletedEntityKind.Account);
            if (!emptyAccounts || state.BootstrapAccountDigest != digest)
            {
                return Decide(state, command, digest, OperationOutcome.Rejected, "BootstrapNotAuthorized");
            }

            return Change(state, command, digest);
        }

        if (MonitoringCommandValidation.IsSystem(command))
        {
            return MonitoringReducer.Apply(state, command);
        }

        AcceptedAccount? actor = RevisionAccounts.Find(state, command.ActorUserId);
        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(actor) || actor!.Revision != command.ActorRevision)
        {
            return Decide(state, command, digest, OperationOutcome.Rejected, "ActorNotActive");
        }

        if (command.Action is "Cancel" or "Replace")
        {
            return Control(state, command, digest);
        }

        return Change(state, command, digest);
    }

    private RevisionImage Control(RevisionImage state, RevisionCommand command, string digest)
    {
        RevisionCommand target = command.Target!;
        string targetDigest = RevisionCodec.Digest(target);
        if (state.Results.TryGetValue(target.OperationId, out CommandResult? original))
        {
            if (original.Digest != targetDigest)
            {
                throw new InvalidDataException("IdempotencyKeyReuse");
            }

            CommandResult actual = original with
            {
                OperationId = command.OperationId, Digest = digest, Reason = "AlreadyDecided",
                ActorRevision = command.ActorRevision
            };
            return state with { Results = state.Results.Add(actual.OperationId, actual) };
        }

        ControlKind kind = ControlKind.Cancel;
        OperationOutcome outcome = OperationOutcome.Cancelled;
        Guid? replacementId = null;
        if (command.Action == "Replace")
        {
            kind = ControlKind.Replace;
            outcome = OperationOutcome.Replaced;
            replacementId = command.Replacement!.OperationId;
            if (state.Results.ContainsKey(replacementId.Value))
            {
                return Decide(state, command, digest, OperationOutcome.Rejected, "ReplacementAlreadyDecided");
            }
        }

        AcceptedControl control = new(command.OperationId, target.OperationId, targetDigest, kind,
            replacementId, state.Index, state.Term);
        state = state with { Controls = state.Controls.Add(target.OperationId, control) };
        state = Decide(state, target, targetDigest, outcome, null, replacedBy: replacementId);
        state = Decide(state, command, digest, outcome, null, replacedBy: replacementId);
        if (command.Replacement is not null)
        {
            state = Execute(state, command.Replacement, RevisionCodec.Digest(command.Replacement));
        }

        return state;
    }

    private RevisionImage Change(RevisionImage state, RevisionCommand command, string digest)
    {
        state.Values.TryGetValue(command.EntityId, out AcceptedValue? current);
        if (current is not null && current.Kind != command.EntityKind)
        {
            return Decide(state, command, digest, OperationOutcome.Rejected, "EntityKindMismatch");
        }

        long revision = current?.Revision ?? 0;
        if (command.BaseRevision > revision)
        {
            return Decide(state, command, digest, OperationOutcome.Rejected, "BaseRevisionAhead");
        }

        if (command.BaseRevision != revision)
        {
            return Conflict(state, command, digest, current!);
        }

        if (current?.Deleted == true)
        {
            return Decide(state, command, digest, OperationOutcome.Rejected, "EntityDeleted");
        }

        if (command.Action == "Delete" && current is null)
        {
            return Decide(state, command, digest, OperationOutcome.Rejected, "EntityMissing");
        }

        AcceptedValue candidate = new(command.EntityId, command.EntityKind, state.Index, command.Value!);
        if (candidate.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container && command.Value is not null)
        {
            DateTimeOffset assignedAt = command.CreatedAtUtc;
            if (current is not null && current.Value.Generation == candidate.Value.Generation)
            {
                assignedAt = current.Value.AssignedAtUtc;
            }

            candidate = candidate with { Value = candidate.Value with { AssignedAtUtc = assignedAt } };
        }
        if (command.Action == "Delete")
        {
            candidate = current! with
            {
                Revision = state.Index, Deleted = true, DeletedAtUtc = command.CreatedAtUtc,
                Value = current.Value with { Enabled = false }
            };
        }

        string? error = RevisionValidation.Validate(state, command, candidate, current);
        if (error is not null)
        {
            AcceptedValue? competing = null;
            if (error == "DuplicateKey")
            {
                competing = RevisionValidation.FindDuplicate(state, candidate);
            }

            if (competing is not null)
            {
                return Conflict(state, command, digest, competing);
            }

            OperationOutcome outcome = OperationOutcome.Rejected;
            if (error == "DependencyDeleted")
            {
                outcome = OperationOutcome.Cancelled;
            }

            return Decide(state, command, digest, outcome, error);
        }

        if (command.ResolvesConflictId.HasValue)
        {
            if (!state.Conflicts.TryGetValue(command.ResolvesConflictId.Value, out ConflictVariant? conflict)
                || conflict.EntityId != command.EntityId || conflict.ResolvedAtUtc.HasValue)
            {
                return Decide(state, command, digest, OperationOutcome.Rejected, "ConflictNotOpen");
            }

            DateTimeOffset resolvedAt = command.CreatedAtUtc;
            if (resolvedAt < conflict.CreatedAtUtc)
            {
                // Порядок задаёт Raft, а календарная дата завершения не предшествует созданию.
                resolvedAt = conflict.CreatedAtUtc;
            }

            conflict = conflict with { ResolutionOperationId = command.OperationId, ResolvedAtUtc = resolvedAt };
            state = state with { Conflicts = state.Conflicts.SetItem(conflict.Id, conflict) };
        }

        state = Put(state, candidate);
        state = MonitoringRecipients.ConfigurationChanged(state, current, candidate, command.CreatedAtUtc);
        if (candidate.Deleted)
        {
            state = DeleteDependents(state, candidate, command.CreatedAtUtc);
        }

        return Decide(state, command, digest, OperationOutcome.Committed, null, state.Index);
    }

    private static RevisionImage DeleteDependents(RevisionImage state, AcceptedValue deleted, DateTimeOffset atUtc)
    {
        foreach (AcceptedValue dependent in state.Values.Values.ToArray())
        {
            if (dependent.Deleted || dependent.Value.ParentId != deleted.Id && dependent.Value.ChatId != deleted.Id)
            {
                continue;
            }

            if (deleted.Kind == DeletedEntityKind.Server && dependent.Kind == DeletedEntityKind.Container)
            {
                state = Put(state, dependent with
                {
                    Revision = state.Index, Value = dependent.Value with { ParentId = null }
                });
            }
            else if (dependent.Kind is DeletedEntityKind.Binding or DeletedEntityKind.ObserverAssignment or DeletedEntityKind.NodeChatRoute)
            {
                state = Put(state, dependent with { Revision = state.Index, Deleted = true, DeletedAtUtc = atUtc });
            }
        }

        return state;
    }

    private static RevisionImage Put(RevisionImage state, AcceptedValue value)
    {
        string key = VersionKey(value.Id, value.Revision);
        return state with
        {
            Values = state.Values.SetItem(value.Id, value), Versions = state.Versions.SetItem(key, value)
        };
    }

    public static string VersionKey(Guid id, long revision)
    {
        return id.ToString("N") + ":" + revision.ToString(CultureInfo.InvariantCulture);
    }

    private static RevisionImage Conflict(RevisionImage state, RevisionCommand command, string digest, AcceptedValue current)
    {
        state.Versions.TryGetValue(VersionKey(command.EntityId, command.BaseRevision), out AcceptedValue? baseline);
        ConflictVariant conflict = new(command.OperationId, command.OperationId, command.EntityId,
            command.BaseRevision, current.Revision, JsonSerializer.Serialize(baseline),
            JsonSerializer.Serialize(current), JsonSerializer.Serialize(command), state.Index, state.Term, command.CreatedAtUtc);
        state = state with { Conflicts = state.Conflicts.Add(conflict.Id, conflict) };
        return Decide(state, command, digest, OperationOutcome.Conflict, "ConcurrentChange", conflictId: conflict.Id);
    }

    private static RevisionImage Decide(RevisionImage state, RevisionCommand command, string digest,
        OperationOutcome outcome, string? reason, long? appliedRevision = null, Guid? conflictId = null, Guid? replacedBy = null)
    {
        Guid? subjectUserId = null;
        if (command.EntityKind == DeletedEntityKind.Account)
        {
            subjectUserId = command.EntityId;
        }

        CommandResult result = new(command.OperationId, digest, outcome, state.Index, state.Term,
            appliedRevision, reason, conflictId, replacedBy, command.ActorRevision)
        {
            SubjectUserId = subjectUserId
        };
        return state with { Results = state.Results.Add(command.OperationId, result) };
    }
}
