using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

internal sealed class OnlineLifecycleFixture : IDisposable
{
    public EnrollmentTrustFixture Trust { get; } = new();
    public RevisionImage Image { get; set; }
    public Guid Boot { get; set; } = Guid.NewGuid();
    public string Challenge { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public DateTimeOffset Now { get; set; }
    public long Epoch { get; set; } = 1;

    public OnlineLifecycleFixture()
    {
        Image = Trust.Accepted();
        Now = Trust.Crypto.Now;
    }

    public OnlineGrant Grant(OnlineGrantPurpose purpose)
    {
        EnrollmentRequest request = Trust.Crypto.Request;
        OperationalCredential credential = Image.Operations.Credentials.Values.Single();
        return new(1, purpose, Image.ClusterId, Image.Generation, Trust.Genesis.GenesisId, null, null,
            request.NodeId, request.StorageGeneration, request.PeerUri, request.SpkiDigest, credential.Id,
            credential.Fingerprint, Trust.Crypto.Epoch, Guid.NewGuid(), Trust.Genesis.AccountOrImportDigest,
            Boot, Challenge, Epoch, Trust.Account.ActorUserId, Image.Values[Trust.Account.ActorUserId].Revision,
            0, 1, Image.Operations.Revision, Image.Operations.MembershipVersion, Image.Index, null, Now, Now.AddMinutes(2), string.Empty);
    }

    public OnlineGrant Sign(OnlineGrant grant)
    {
        byte[] signature = Trust.Crypto.IssuerKey.SignData(OnlineLifecycleCommands.Canonical(grant),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return grant with { Signature = Convert.ToBase64String(signature) };
    }

    public RevisionCommand Command(string action, OnlineGrant grant)
    {
        Guid operationId = grant.GrantId;
        if (action is "ConsumeOnboardingActivation" or "EnableOnlineGrant")
        {
            operationId = OnlineLifecycleCommands.ConsumeId(grant.GrantId);
        }

        return new()
        {
            Schema = 3, ClusterId = Image.ClusterId, ClusterGeneration = Image.Generation,
            OriginNodeId = Trust.Genesis.NodeId, OperationId = operationId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = grant.NodeId, EntityKind = DeletedEntityKind.Node, Action = action, BaseRevision = grant.OperationsRevision,
            CreatedAtUtc = Now, SystemSource = "OnlineLifecycle",
            OnlineLifecycle = new(1, Trust.Genesis.StorageGeneration, grant, Now, 1)
        };
    }

    public RevisionImage Apply(string action, OnlineGrant grant)
    {
        Image = new RevisionReducer().Apply(Image, Command(action, grant), Image.Index + 1, 1);
        return Image;
    }

    public void Register()
    {
        OnlineGrant process = Sign(Grant(OnlineGrantPurpose.Process));
        Apply("RegisterOnlineProcess", process);
        if (Image.Results[process.GrantId].Outcome != OperationOutcome.Committed)
        {
            throw new InvalidOperationException("TestProcessPreparationRejected");
        }
    }

    public void Dispose()
    {
        Trust.Dispose();
    }
}
