using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class OnlineProcessRenewalTests
{
    [Fact]
    public void ActiveProcessRenewsWithPeerKeyAfterIssuerRetirementWithoutIssuerPrivateKey()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        AcceptedEnrollmentIssuer issuer = fixture.Image.OnboardingContracts!.Issuers![fixture.Trust.Crypto.Epoch];
        fixture.Image = fixture.Image with { OnboardingContracts = fixture.Image.OnboardingContracts with
        {
            Issuers = fixture.Image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RetiredAtUtc = fixture.Now })
        } };
        fixture.Now = fixture.Now.AddMinutes(1);
        OnlineGrant renewal = Sign(fixture, fixture.Grant(OnlineGrantPurpose.Process));

        // Act
        bool renewalProof = OnlineGrantSignatures.Verify(fixture.Image, renewal, fixture.Now, processRenewal: true);
        bool issuerProof = OnlineGrantSignatures.Verify(fixture.Image, renewal, fixture.Now);
        fixture.Apply("RenewOnlineProcess", renewal);
        CommandResult first = fixture.Image.Results[renewal.GrantId];
        RevisionImage retry = new RevisionReducer().Apply(fixture.Image, fixture.Command("RenewOnlineProcess", renewal), fixture.Image.Index + 1, 1);

        // Assert
        Assert.True(renewalProof);
        Assert.False(issuerProof);
        Assert.Equal(OperationOutcome.Committed, first.Outcome);
        Assert.Equal(first, retry.Results[renewal.GrantId]);
        Assert.True(fixture.Image.OnboardingContracts!.OnlineGrants![renewal.GrantId].ProcessRenewal);
        Assert.Equal(fixture.Now.AddMinutes(2), fixture.Image.OnboardingContracts.OnlineProcesses![renewal.NodeId].LeaseUntilUtc);
        Assert.False(fixture.Image.Operations.Nodes[renewal.NodeId].Activated);
        OnboardingContractSnapshot.Validate(fixture.Image);
    }

    [Theory]
    [InlineData("unregistered")]
    [InlineData("boot")]
    [InlineData("challenge")]
    [InlineData("epoch")]
    [InlineData("recovery")]
    [InlineData("expiry")]
    [InlineData("actor")]
    [InlineData("credential")]
    [InlineData("issuer")]
    public void PeerProofCannotCreateOrReplaceProcessOrBypassRevocation(string mutation)
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        if (mutation != "unregistered")
        {
            fixture.Register();
        }

        OnlineGrant request = fixture.Grant(OnlineGrantPurpose.Process);
        switch (mutation)
        {
            case "boot": request = request with { BootId = Guid.NewGuid() }; break;
            case "challenge": request = request with { BootChallenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }; break;
            case "epoch": request = request with { ProcessEpoch = 2 }; break;
            case "recovery": request = request with { RecoveryVersion = 2 }; break;
            case "expiry": fixture.Now = fixture.Now.AddMinutes(2); request = request with { IssuedAtUtc = fixture.Now, ExpiresAtUtc = fixture.Now.AddMinutes(2) }; break;
            case "actor": request = request with { ActorRevision = request.ActorRevision + 1 }; break;
            case "credential":
                OperationalCredential credential = fixture.Image.Operations.Credentials[request.CredentialId];
                fixture.Image = fixture.Image with { Operations = fixture.Image.Operations with
                {
                    Credentials = fixture.Image.Operations.Credentials.SetItem(credential.Id, credential with { RevokedAtUtc = fixture.Now })
                } };
                break;
            case "issuer":
                AcceptedEnrollmentIssuer issuer = fixture.Image.OnboardingContracts!.Issuers![request.IssuerEpoch];
                fixture.Image = fixture.Image with { OnboardingContracts = fixture.Image.OnboardingContracts with
                {
                    Issuers = fixture.Image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = fixture.Now })
                } };
                break;
        }

        OnlineGrant renewal = Sign(fixture, request);

        // Act
        fixture.Apply("RenewOnlineProcess", renewal);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, fixture.Image.Results[renewal.GrantId].Outcome);
        Assert.False(fixture.Image.OnboardingContracts!.OnlineGrants?.ContainsKey(renewal.GrantId) == true);
    }

    [Fact]
    public void RenewalDomainCannotAuthorizeActivationEnableOrNewProcess()
    {
        // Arrange
        using OnlineLifecycleFixture fixture = new();
        fixture.Register();
        OnlineGrant renewal = Sign(fixture, fixture.Grant(OnlineGrantPurpose.Process));

        // Act
        bool registration = OnlineGrantSignatures.Verify(fixture.Image, renewal, fixture.Now);
        bool activation = OnlineGrantSignatures.Verify(fixture.Image, renewal with { Purpose = OnlineGrantPurpose.Activation }, fixture.Now, processRenewal: true);
        bool enable = OnlineGrantSignatures.Verify(fixture.Image, renewal with { Purpose = OnlineGrantPurpose.Enable }, fixture.Now, processRenewal: true);

        // Assert
        Assert.False(registration);
        Assert.False(activation);
        Assert.False(enable);
    }

    private static OnlineGrant Sign(OnlineLifecycleFixture fixture, OnlineGrant grant)
    {
        byte[] signature = fixture.Trust.Crypto.PeerKey.SignData(OnlineLifecycleCommands.Canonical(grant, processRenewal: true),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return grant with { Signature = Convert.ToBase64String(signature) };
    }
}
