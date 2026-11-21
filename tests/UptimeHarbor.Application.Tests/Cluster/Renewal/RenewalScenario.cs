using System.Collections.Immutable;
using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class RenewalScenario
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    public RevisionImage State { get; set; }
    public VoterCredentialRenewal Intent { get; set; }
    public Guid Node { get; } = Guid.NewGuid();
    public Guid Storage { get; } = Guid.NewGuid();
    public OperationalCredential Old => State.Operations.Credentials[Intent.PreviousCredentialId];
    public VoterCredentialRotation Rotation => State.OnboardingContracts!.VoterCredentialRotations![Intent.Issuance.IssueId];

    public RenewalScenario()
    {
        Guid epoch = Guid.NewGuid();
        Guid previous = Guid.NewGuid();
        EnrollmentIssuance template = new(1, Guid.NewGuid(), epoch, "0123456789ABCDEF0123456789ABCDEF", Node, Storage,
            "https://voter.invalid/internal/raft", new string('B', 64), new string('C', 64), Now.AddMinutes(-2), Now.AddDays(30));
        OperationalCredential old = new(previous, Node, Hash([1, 2, 3]), Convert.ToBase64String([1, 2, 3]), Now.AddDays(7), null, null);
        Intent = new(previous, old.Fingerprint, new string('A', 64), 0, template);
        AcceptedEnrollmentIssuer issuer = new(epoch, Node, Storage, EnrollmentIssuer.PEER_PURPOSE,
            Convert.ToBase64String([4, 5, 6]), Hash([4, 5, 6]), new string('D', 64), Now.AddDays(-1), Now.AddYears(2), 1);
        OperationalNode node = new(Node, Storage, template.PeerUri, "voter", true, true, false, 1);
        State = new(Guid.NewGuid(), 1)
        {
            Index = 1, Term = 1,
            OnboardingContracts = new() { Issuers = ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>.Empty.Add(epoch, issuer) },
            Operations = new()
            {
                Nodes = ImmutableDictionary<Guid, OperationalNode>.Empty.Add(Node, node),
                Credentials = ImmutableDictionary<Guid, OperationalCredential>.Empty.Add(previous, old),
                Revision = 1, MembershipVersion = 9
            }
        };
        OnboardingContractSnapshot.Validate(State);
    }

    public RevisionCommand Command(string action)
    {
        OperationalCredential? credential = null;
        string? proof = null;
        if (action == "BindVoterCredential")
        {
            credential = new(Intent.Issuance.IssueId, Node, Hash([7, 8, 9]), Convert.ToBase64String([7, 8, 9]),
                Intent.Issuance.NotAfterUtc, null, null);
        }
        else if (action == "RetireOldCredential")
        {
            proof = new string('E', 64);
        }

        return new()
        {
            Schema = 3, ClusterId = State.ClusterId, ClusterGeneration = State.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = Node, ActorUserId = Guid.Empty, ActorRevision = 0, EntityId = Node,
            EntityKind = DeletedEntityKind.Node, Action = action, BaseRevision = Intent.PreviousCredentialRevision,
            CreatedAtUtc = Now.AddYears(1), SystemSource = "VoterCredentialLifecycle",
            EnrollmentTrust = new(1, Storage, Intent.Issuance.IssuerEpoch, null, null, null, null, 0, null)
            {
                CoordinatorAtUtc = Now, CoordinatorTerm = State.Term, Rotation = new(Intent, credential, proof)
            }
        };
    }

    public CommandResult Apply(RevisionCommand command)
    {
        State = new RevisionReducer().Apply(State, command, State.Index + 1, State.Term);
        return State.Results[command.OperationId];
    }

    public RevisionCommand Commit(string action)
    {
        RevisionCommand command = Command(action);
        Assert.Equal(OperationOutcome.Committed, Apply(command).Outcome);
        return command;
    }

    public static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
