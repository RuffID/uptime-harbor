using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentAcceptedBindingTests
{
    [Fact]
    public void AcceptedIssuanceRequiresRegisteredIssuerAndRemainsOutsideOperationalCredentials()
    {
        // Arrange
        using EnrollmentJoinScenario scenario = new();
        RevisionCommand command = scenario.Authorize();

        // Act
        RevisionImage image = scenario.Apply(command);
        long index = EnrollmentSigningPolicy.Require(image, scenario.Request, scenario.Template, scenario.LocalIssuer,
            scenario.First.Genesis.NodeId, scenario.First.Genesis.StorageGeneration, scenario.Now);
        OnboardingSnapshot.Validate(image);

        // Assert
        Assert.Equal(OperationOutcome.Committed, image.Results[command.OperationId].Outcome);
        Assert.Equal(image.Index, index);
        Assert.Single(image.Operations.Nodes);
        Assert.Single(image.Operations.Credentials);
        Assert.DoesNotContain(image.Operations.Credentials.Values, item => item.NodeId == scenario.Request.NodeId);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("retired")]
    [InlineData("revoked")]
    [InlineData("actor")]
    public void IssuanceRechecksIssuerAndDelegationAtAcceptedPosition(string mutation)
    {
        // Arrange
        using EnrollmentJoinScenario scenario = new();
        RevisionImage image = scenario.Image;
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![scenario.First.Crypto.Epoch];
        RevisionCommand command = scenario.Authorize();
        if (mutation == "unknown")
        {
            command = command with { Onboarding = command.Onboarding! with { Issuance = scenario.Template with { IssuerEpoch = Guid.NewGuid() } } };
        }
        else if (mutation == "actor")
        {
            AcceptedValue account = image.Values[scenario.First.Account.EntityId];
            image = image with { Values = image.Values.SetItem(account.Id, account with { Value = account.Value with { Enabled = false } }) };
        }
        else
        {
            if (mutation == "retired")
            {
                issuer = issuer with { RetiredAtUtc = scenario.Now };
            }
            else
            {
                issuer = issuer with { RevokedAtUtc = scenario.Now };
            }

            image = image with { OnboardingContracts = image.OnboardingContracts with { Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer) } };
        }

        // Act
        RevisionImage result = new RevisionReducer().Apply(image, command, image.Index + 1, 1);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Results[command.OperationId].Outcome);
        Assert.Null(result.Onboarding!.Invitations[scenario.Request.InvitationId].Attempt!.Issuance);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("template")]
    [InlineData("epoch")]
    [InlineData("local")]
    [InlineData("lease")]
    [InlineData("maintenance")]
    public void SigningPolicyDoesNotTurnAcceptedIssueIntoPermanentOrForeignAuthority(string mutation)
    {
        // Arrange
        using EnrollmentJoinScenario scenario = new();
        RevisionImage image = scenario.Apply(scenario.Authorize());
        EnrollmentRequest request = scenario.Request;
        EnrollmentIssuance template = scenario.Template;
        EnrollmentIssuerReference local = scenario.LocalIssuer;
        DateTimeOffset now = scenario.Now;
        switch (mutation)
        {
            case "digest": request = request with { CsrDigest = new string('F', 64) }; break;
            case "template": template = template with { Serial = new string('A', 32) }; break;
            case "epoch": local = local with { Epoch = Guid.NewGuid() }; break;
            case "local": local = local with { CertificateDigest = new string('F', 64) }; break;
            case "lease": now = now.AddMinutes(2); break;
            case "maintenance": image = image with { Operations = image.Operations with { MaintenanceId = Guid.NewGuid() } }; break;
        }

        // Act
        Exception? rejected = Record.Exception(() => EnrollmentSigningPolicy.Require(image, request, template, local,
            scenario.First.Genesis.NodeId, scenario.First.Genesis.StorageGeneration, now));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(rejected);
    }

    [Fact]
    public void ProcessTakeoverWaitsLeaseAndRejectsOldChallengeAndOldEpoch()
    {
        // Arrange
        using EnrollmentJoinScenario scenario = new();
        string nextChallenge = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Guid nextBoot = Guid.NewGuid();
        RevisionCommand early = scenario.Command("ResumeOnboardingAttempt", scenario.Step() with
        {
            BootId = nextBoot, BootChallenge = nextChallenge, ProcessEpoch = 2
        });

        // Act
        RevisionImage refused = scenario.Apply(early);
        scenario.Now = scenario.Now.AddMinutes(2);
        RevisionCommand resume = scenario.Command("ResumeOnboardingAttempt", scenario.Step() with
        {
            BootId = nextBoot, BootChallenge = nextChallenge, ProcessEpoch = 2
        });
        RevisionImage resumed = scenario.Apply(resume);
        RevisionCommand stale = scenario.Command("RenewOnboardingProcess", scenario.Step());
        RevisionImage replayed = scenario.Apply(stale);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, refused.Results[early.OperationId].Outcome);
        Assert.Equal(OperationOutcome.Committed, resumed.Results[resume.OperationId].Outcome);
        Assert.Equal(OperationOutcome.Rejected, replayed.Results[stale.OperationId].Outcome);
        OnboardingAttempt attempt = replayed.Onboarding!.Invitations[scenario.Request.InvitationId].Attempt!;
        Assert.Equal(nextChallenge, attempt.BootChallenge);
        Assert.Equal(2, attempt.ProcessEpoch);
        Assert.Equal(nextBoot, attempt.BootId);
    }
}
