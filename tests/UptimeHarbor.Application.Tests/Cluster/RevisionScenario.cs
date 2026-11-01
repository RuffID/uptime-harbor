using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Application.Cluster;

public class RevisionScenario
{
    public Guid Actor { get; } = Guid.NewGuid();
    public Guid OtherActor { get; } = Guid.NewGuid();
    public Guid Node { get; } = Guid.NewGuid();
    public Guid Role { get; } = Guid.NewGuid();
    public RevisionImage State { get; set; }
    public RevisionReducer Reducer { get; } = new();

    public RevisionScenario()
    {
        AcceptedValue first = new(Actor, DeletedEntityKind.Account, 1, AccountValue("admin"));
        AcceptedValue second = new(OtherActor, DeletedEntityKind.Account, 2, AccountValue("other"));
        State = new RevisionImage(Guid.NewGuid(), 1)
        {
            Index = 2, Term = 1, Digest = new string('A', 64),
            Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(first.Id, first).Add(second.Id, second),
            Versions = ImmutableDictionary<string, AcceptedValue>.Empty
                .Add(RevisionReducer.VersionKey(first.Id, 1), first).Add(RevisionReducer.VersionKey(second.Id, 2), second)
        };
    }

    public ConfigurationValue AccountValue(string name)
    {
        return new ConfigurationValue
        {
            Name = name, RoleId = Role, PasswordHash = "protected-hash", SecurityStamp = "security",
            ConcurrencyStamp = "concurrency", Enabled = true
        };
    }

    public RevisionCommand Server(Guid? id = null, long revision = 0, string name = "server")
    {
        Guid generation = Guid.NewGuid();
        if (id.HasValue && State.Values.TryGetValue(id.Value, out AcceptedValue? current))
        {
            generation = current.Value.Generation;
        }

        return Command(id ?? Guid.NewGuid(), DeletedEntityKind.Server, revision, new ConfigurationValue
        {
            Name = name, Endpoint = "https://test.invalid/health", Generation = generation
        });
    }

    public RevisionCommand Command(Guid id, DeletedEntityKind kind, long revision, ConfigurationValue? value)
    {
        return new RevisionCommand
        {
            ClusterId = State.ClusterId, ClusterGeneration = State.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = Node, ActorUserId = Actor, ActorRevision = State.Values[Actor].Revision,
            EntityId = id, EntityKind = kind, Action = "Upsert", BaseRevision = revision,
            Value = value, CreatedAtUtc = DateTimeOffset.UnixEpoch
        };
    }

    public RevisionCommand Control(RevisionCommand target, RevisionCommand? replacement = null)
    {
        string action = "Cancel";
        if (replacement is not null)
        {
            action = "Replace";
        }

        return Command(target.EntityId, target.EntityKind, target.BaseRevision, null) with
        {
            Action = action, Target = target, Replacement = replacement
        };
    }

    public CommandResult Apply(RevisionCommand command, long term = 1)
    {
        RevisionImage next = Reducer.Apply(State, command, State.Index + 1, term);
        RevisionWriteSetFactory.Create(State, next, Node);
        State = next;
        return State.Results[command.OperationId];
    }
}
