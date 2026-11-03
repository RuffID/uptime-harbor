using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

/// <summary>Реальный reducer, fake coordinator time. Не заменяет majority/transport/native Join.</summary>
public class EnrollmentJoinScenario : IDisposable
{
    public EnrollmentTrustFixture First { get; } = new();
    public EnrollmentCertificateFixture Join { get; } = new();
    public RevisionImage Image { get; private set; }
    public EnrollmentRequest Request { get; }
    public EnrollmentIssuance Template { get; }
    public Guid BootId { get; } = Guid.NewGuid();
    public string BootChallenge { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public DateTimeOffset Now { get; set; }
    public EnrollmentIssuerReference LocalIssuer { get; }

    public EnrollmentJoinScenario()
    {
        Image = First.Accepted();
        Now = First.Crypto.Now;
        Request = Join.Request with
        {
            ClusterId = Image.ClusterId, Generation = Image.Generation, PeerUri = "https://join.example.test/internal/raft"
        };
        Template = Join.Template with { IssuerEpoch = First.Crypto.Epoch, PeerUri = Request.PeerUri };
        LocalIssuer = new(First.Crypto.Epoch, First.Genesis.InstallIdentity, Image.ClusterId, Image.Generation,
            First.Genesis.NodeId, First.Genesis.StorageGeneration, "protected-local-reference", First.Crypto.Issuer.RawData,
            First.Genesis.IssuerCertificateDigest, First.Genesis.IssuerSpkiDigest);
        Prepare(Command("IssueInvitation", new()
        {
            StorageGeneration = First.Genesis.StorageGeneration, InvitationId = Request.InvitationId,
            Verifier = new string('A', 64), ReauthenticatedAtUtc = Now
        }));
        Prepare(Command("ReserveInvitation", Step() with
        {
            Verifier = new string('A', 64), Request = Request, RequestDigest = Application.Cluster.Onboarding.EnrollmentRequestCodec.Digest(Request)
        }));
    }

    private void Prepare(RevisionCommand command)
    {
        Apply(command);
        CommandResult result = Image.Results[command.OperationId];
        if (result.Outcome != Domain.Cluster.OperationOutcome.Committed)
        {
            throw new InvalidOperationException("ScenarioPrerequisiteRejected/" + result.Reason);
        }
    }

    public OnboardingCommand Step()
    {
        return new()
        {
            StorageGeneration = First.Genesis.StorageGeneration, InvitationId = Request.InvitationId, AttemptId = Request.AttemptId,
            BootId = BootId, BootChallenge = BootChallenge, ProcessEpoch = 1, CoordinatorAtUtc = Now, CoordinatorTerm = 1
        };
    }

    public RevisionCommand Command(string action, OnboardingCommand payload)
    {
        bool administrator = action == "IssueInvitation";
        Guid actor = Guid.Empty;
        long revision = 0;
        string? source = "OnboardingCoordinator";
        if (administrator)
        {
            actor = First.Account.ActorUserId;
            revision = Image.Values[actor].Revision;
            source = null;
        }

        return new()
        {
            Schema = 3, ClusterId = Image.ClusterId, ClusterGeneration = Image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = First.Genesis.NodeId, ActorUserId = actor, ActorRevision = revision,
            EntityId = Request.InvitationId, EntityKind = Domain.Cluster.DeletedEntityKind.Node, Action = action,
            BaseRevision = Image.Onboarding?.Revision ?? 0, SystemSource = source, CreatedAtUtc = Now,
            Onboarding = payload with { CoordinatorAtUtc = Now, CoordinatorTerm = 1 }
        };
    }

    public RevisionImage Apply(RevisionCommand command)
    {
        Image = new RevisionReducer().Apply(Image, command, Image.Index + 1, 1);
        return Image;
    }

    public RevisionCommand Authorize()
    {
        return Command("AuthorizeEnrollmentCertificate", Step() with { IssueId = Template.IssueId, Issuance = Template });
    }

    public void Dispose()
    {
        Join.Dispose();
        First.Dispose();
    }
}
