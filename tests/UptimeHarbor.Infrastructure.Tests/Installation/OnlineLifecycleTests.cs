using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class OnlineLifecycleTests
{
    [Fact]
    public void ProcessActivationAndEnableHaveSeparateAcceptedConsumptionAndStableRetryResults()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        OnlineGrant activation = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Activation));

        // Act
        bool signature = OnlineGrantSignatures.Verify(fixture.Image, activation, fixture.Now);
        fixture.Apply("AuthorizeOnboardingActivation", activation);

        // Assert
        Assert.True(signature);
        Assert.False(fixture.Image.Operations.Nodes[activation.NodeId].Activated);
        Assert.Null(fixture.Image.OnboardingContracts!.OnlineGrants![activation.GrantId].ConsumedIndex);

        // Act
        fixture.Apply("ConsumeOnboardingActivation", activation);
        OnlineGrant enable = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Enable));
        fixture.Apply("AuthorizeOnlineEnable", enable);
        fixture.Apply("EnableOnlineGrant", enable);
        CommandResult original = fixture.Image.Results[OnlineLifecycleCommands.ConsumeId(enable.GrantId)];
        RevisionCommand retry = fixture.Command("EnableOnlineGrant", enable);
        RevisionImage repeated = new RevisionReducer().Apply(fixture.Image, retry, fixture.Image.Index + 1, 1);

        // Assert
        Assert.True(fixture.Image.Operations.Nodes[activation.NodeId].Activated);
        Assert.Equal(OperationOutcome.Committed, original.Outcome);
        Assert.Equal(original, repeated.Results[original.OperationId]);
        Assert.Equal(original.Index, fixture.Image.OnboardingContracts!.OnlineGrants![enable.GrantId].ConsumedIndex);
        Assert.NotEqual(enable.GrantId, activation.GrantId);
        OnboardingContractSnapshot.Validate(fixture.Image);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("boot")]
    [InlineData("challenge")]
    [InlineData("recovery")]
    [InlineData("membership")]
    [InlineData("operations")]
    [InlineData("actor")]
    [InlineData("credential")]
    [InlineData("issuer")]
    [InlineData("maintenance")]
    [InlineData("expired")]
    [InlineData("nonvoter")]
    public void StaleOrRevokedAuthorityCannotAuthorizeActivation(string mutation)
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        OnlineGrant grant = fixture.Grant(OnlineGrantPurpose.Activation);
        OperationalCredential credential = fixture.Image.Operations.Credentials[grant.CredentialId];
        switch (mutation)
        {
            case "process":
                grant = grant with { ProcessEpoch = 2 };
                break;
            case "boot":
                grant = grant with { BootId = Guid.NewGuid() };
                break;
            case "challenge":
                grant = grant with { BootChallenge = Convert.ToBase64String(new byte[32]) };
                break;
            case "recovery":
                grant = grant with { RecoveryVersion = 2 };
                break;
            case "membership":
                grant = grant with { MembershipVersion = grant.MembershipVersion + 1 };
                break;
            case "operations":
                grant = grant with { OperationsRevision = grant.OperationsRevision + 1 };
                break;
            case "actor":
                grant = grant with { ActorRevision = grant.ActorRevision + 1 };
                break;
            case "credential":
                fixture.Image = fixture.Image with { Operations = fixture.Image.Operations with
                {
                    Credentials = fixture.Image.Operations.Credentials.SetItem(credential.Id, credential with { RevokedAtUtc = fixture.Now })
                } };
                break;
            case "issuer":
                AcceptedEnrollmentIssuer issuer = fixture.Image.OnboardingContracts!.Issuers![grant.IssuerEpoch];
                fixture.Image = fixture.Image with { OnboardingContracts = fixture.Image.OnboardingContracts with
                {
                    Issuers = fixture.Image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = fixture.Now })
                } };
                break;
            case "maintenance":
                fixture.Image = fixture.Image with { Operations = fixture.Image.Operations with { MaintenanceId = Guid.NewGuid() } };
                break;
            case "expired":
                fixture.Now = fixture.Now.AddMinutes(2);
                break;
            case "nonvoter":
                OperationalNode node = fixture.Image.Operations.Nodes[grant.NodeId];
                fixture.Image = fixture.Image with { Operations = fixture.Image.Operations with
                {
                    Nodes = fixture.Image.Operations.Nodes.SetItem(node.NodeId, node with { Voting = false })
                } };
                break;
        }

        grant = fixture.Sign(grant);

        // Act
        fixture.Apply("AuthorizeOnboardingActivation", grant);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, fixture.Image.Results[grant.GrantId].Outcome);
        Assert.False(fixture.Image.Operations.Nodes[grant.NodeId].Activated);
        Assert.False(fixture.Image.OnboardingContracts!.OnlineGrants!.ContainsKey(grant.GrantId));
    }

    [Fact]
    public void SignatureCannotChangePurposeIdentityOrRequestAndDoesNotReplaceAcceptedAuthorization()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        OnlineGrant grant = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Activation));

        // Act
        bool original = OnlineGrantSignatures.Verify(fixture.Image, grant, fixture.Now);
        bool changedPurpose = OnlineGrantSignatures.Verify(fixture.Image, grant with { Purpose = OnlineGrantPurpose.Enable }, fixture.Now);
        bool changedRequest = OnlineGrantSignatures.Verify(fixture.Image, grant with { RequestDigest = new string('F', 64) }, fixture.Now);
        bool changedKey = OnlineGrantSignatures.Verify(fixture.Image, grant with { SpkiDigest = new string('F', 64) }, fixture.Now);
        fixture.Apply("ConsumeOnboardingActivation", grant);

        // Assert
        Assert.True(original);
        Assert.False(changedPurpose);
        Assert.False(changedRequest);
        Assert.False(changedKey);
        Assert.Equal(OperationOutcome.Rejected, fixture.Image.Results[OnlineLifecycleCommands.ConsumeId(grant.GrantId)].Outcome);
        Assert.False(fixture.Image.Operations.Nodes[grant.NodeId].Activated);
    }

    [Fact]
    public void NewProcessWaitsPreviousLeaseAndCannotReuseOldEpochOrBootChallenge()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        OnlineGrant old = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Activation));
        fixture.Boot = Guid.NewGuid();
        fixture.Challenge = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        fixture.Epoch = 2;
        OnlineGrant early = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Process));

        // Act
        fixture.Apply("RegisterOnlineProcess", early);
        fixture.Now = fixture.Now.AddMinutes(2);
        OnlineGrant next = fixture.Sign(fixture.Grant(OnlineGrantPurpose.Process));
        fixture.Apply("RegisterOnlineProcess", next);
        bool stale = OnlineGrantPolicy.Current(fixture.Image, old, old.IssuedAtUtc);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, fixture.Image.Results[early.GrantId].Outcome);
        Assert.Equal(OperationOutcome.Committed, fixture.Image.Results[next.GrantId].Outcome);
        Assert.False(stale);
        Assert.Equal(2, fixture.Image.OnboardingContracts!.OnlineProcesses![next.NodeId].ProcessEpoch);
    }

    [Fact]
    public void ActivatedGenesisCannotBypassOnlineGrantUsingLegacyLifecycleCommand()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        RevisionCommand command = ClusterLifecycleCommands.Create(fixture.Image, fixture.Trust.Genesis.NodeId,
            fixture.Trust.Genesis.StorageGeneration, "ActivateNode", fixture.Trust.Genesis.NodeId, new ClusterOperation(), fixture.Now);

        // Act
        RevisionImage refused = new RevisionReducer().Apply(fixture.Image, command, fixture.Image.Index + 1, 1);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, refused.Results[command.OperationId].Outcome);
        Assert.False(refused.Operations.Nodes[command.EntityId].Activated);
    }
}
