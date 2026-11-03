using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Application.Cluster;

/// <summary>Симуляция принятого порядка, без настоящего admission, транспорта и provider.</summary>
public class OnboardingScenario
{
    public RevisionScenario Journal { get; } = new();
    public Guid Storage { get; } = Guid.NewGuid();
    public Guid InvitationId { get; } = Guid.NewGuid();
    public Guid Code { get; } = InvitationSecrets.CreateCode();
    public Guid Boot { get; } = Guid.NewGuid();
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    public EnrollmentRequest Request { get; }
    public RevisionImage State => Journal.State;
    public OnboardingInvitation Invitation => State.Onboarding!.Invitations[InvitationId];

    public OnboardingScenario()
    {
        OperationalNode local = new(Journal.Node, Storage, "https://first.invalid/internal/raft", "first", true, true, false, 1);
        Journal.State = Journal.State with
        {
            Operations = new ClusterOperationsImage { Nodes = new ClusterOperationsImage().Nodes.Add(local.NodeId, local), MembershipVersion = 1 },
            Monitoring = Journal.State.Monitoring with { Nodes = Journal.State.Monitoring.Nodes.Add(local.NodeId, Storage) }
        };
        Request = new(1, State.ClusterId, State.Generation, InvitationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "https://second.invalid/internal/raft", new string('B', 64), new string('C', 64));
    }

    public RevisionCommand Issue()
    {
        return Command("IssueInvitation", new OnboardingCommand
        {
            StorageGeneration = Storage, InvitationId = InvitationId,
            Verifier = InvitationSecrets.Verifier(State.ClusterId, State.Generation, InvitationId, Code), ReauthenticatedAtUtc = Now
        }) with { OperationId = InvitationId };
    }

    public RevisionCommand Reserve(EnrollmentRequest? request = null)
    {
        EnrollmentRequest binding = request ?? Request;
        return Command("ReserveInvitation", new OnboardingCommand
        {
            StorageGeneration = Storage, InvitationId = InvitationId, AttemptId = binding.AttemptId,
            Request = binding, RequestDigest = EnrollmentRequestCodec.Digest(binding), BootId = Boot, ProcessEpoch = 1,
            Verifier = InvitationSecrets.Verifier(State.ClusterId, State.Generation, InvitationId, Code)
        });
    }

    public RevisionCommand Step(string action)
    {
        OnboardingAttempt attempt = Invitation.Attempt!;
        return Command(action, new OnboardingCommand
        {
            StorageGeneration = Storage, InvitationId = InvitationId, AttemptId = Request.AttemptId,
            BootId = attempt.BootId, ProcessEpoch = attempt.ProcessEpoch
        });
    }

    public RevisionCommand Command(string action, OnboardingCommand data)
    {
        bool coordinator = OnboardingCommands.IsCoordinator(new RevisionCommand
        {
            ClusterId = State.ClusterId, ClusterGeneration = 1, OperationId = Guid.NewGuid(), OriginNodeId = Journal.Node,
            ActorUserId = Journal.Actor, ActorRevision = 1, EntityId = data.InvitationId, EntityKind = DeletedEntityKind.Node,
            Action = action, BaseRevision = 0, CreatedAtUtc = Now
        });
        Guid actor = Journal.Actor;
        long actorRevision = State.Values[actor].Revision;
        string? source = null;
        if (coordinator)
        {
            actor = Guid.Empty;
            actorRevision = 0;
            source = "OnboardingCoordinator";
        }

        return new RevisionCommand
        {
            Schema = 2, ClusterId = State.ClusterId, ClusterGeneration = State.Generation,
            OperationId = Guid.NewGuid(), OriginNodeId = Journal.Node, ActorUserId = actor, ActorRevision = actorRevision,
            EntityId = data.InvitationId, EntityKind = DeletedEntityKind.Node, Action = action,
            BaseRevision = State.Onboarding?.Revision ?? 0, CreatedAtUtc = Now, SystemSource = source, Onboarding = data
        };
    }

    public CommandResult Apply(RevisionCommand command)
    {
        return Journal.Apply(command with { Onboarding = command.Onboarding! with { CoordinatorAtUtc = Now, CoordinatorTerm = State.Term } });
    }

    public void PrepareReservation()
    {
        Xunit.Assert.Equal(OperationOutcome.Committed, Apply(Issue()).Outcome);
        Xunit.Assert.Equal(OperationOutcome.Committed, Apply(Reserve()).Outcome);
    }

    public void Admit()
    {
        PrepareReservation();
        Guid issueId = Guid.NewGuid();
        RevisionCommand authorize = Step("AuthorizeEnrollmentCertificate");
        authorize = authorize with { Onboarding = authorize.Onboarding! with
        {
            IssueId = issueId,
            Issuance = new(1, issueId, Guid.NewGuid(), new string('A', 32), Request.NodeId, Request.StorageGeneration,
                Request.PeerUri, Request.SpkiDigest, Request.CsrDigest, Now, Now.AddDays(30))
        } };
        Xunit.Assert.Equal(OperationOutcome.Committed, Apply(authorize).Outcome);
        RevisionCommand bind = Step("BindEnrollmentCredential");
        bind = bind with { Onboarding = bind.Onboarding! with { IssueId = issueId, CertificateDigest = new string('D', 64) } };
        Xunit.Assert.Equal(OperationOutcome.Committed, Apply(bind).Outcome);
        RevisionCommand admit = Step("AdmitInvitedNode");
        admit = admit with { Onboarding = admit.Onboarding! with
        {
            Node = new(Request.NodeId, Request.StorageGeneration, Request.PeerUri, "second", false, false, false, 0),
            Credential = new(Guid.NewGuid(), Request.NodeId, new string('D', 64), "test-only-public-certificate",
                Now.AddDays(30), admit.OperationId, null)
        } };
        Xunit.Assert.Equal(OperationOutcome.Committed, Apply(admit).Outcome);
    }
}
