using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialFreshAuthorityTests
{
    [Theory]
    [InlineData("old-revoked")]
    [InlineData("old-expired")]
    [InlineData("old-retired")]
    [InlineData("issuer-revoked")]
    [InlineData("issuer-retired")]
    [InlineData("issuer-expired")]
    [InlineData("issuer-missing")]
    [InlineData("issuer-owner-removed")]
    [InlineData("issuer-purpose")]
    [InlineData("voter-removed")]
    [InlineData("nonvoter")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("maintenance")]
    [InlineData("membership")]
    [InlineData("process")]
    [InlineData("lease-expired")]
    [InlineData("proof-expired")]
    [InlineData("proof-future")]
    [InlineData("authorization-index")]
    [InlineData("authorization-digest")]
    [InlineData("authorization-operation")]
    [InlineData("authorization-intent")]
    [InlineData("authorization-result")]
    [InlineData("bound")]
    [InlineData("projection")]
    [InlineData("compatibility")]
    public async Task NewAndReplaySigningRequireCurrentAuthority(string mutation)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact original = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);
        RevisionImage image = fixture.Scenario.State;
        OperationalCredential old = image.Operations.Credentials[fixture.Request.Renewal.PreviousCredentialId];
        AcceptedEnrollmentIssuer issuer = Assert.Single(image.OnboardingContracts!.Issuers!).Value;
        OperationalNode node = image.Operations.Nodes[fixture.SubjectId];
        OnlineProcessLease process = image.OnboardingContracts.OnlineProcesses![fixture.SubjectId];
        VoterCredentialRotation rotation = Assert.Single(image.OnboardingContracts.VoterCredentialRotations!).Value;
        CommandResult result = image.Results[rotation.AuthorizationOperationId];
        switch (mutation)
        {
            case "old-revoked":
                old = old with { RevokedAtUtc = fixture.Time.Now };
                break;
            case "old-expired":
                fixture.Time.Now = old.ExpiresAtUtc;
                break;
            case "old-retired":
                rotation = rotation with { CredentialRevision = image.Index, BoundAtUtc = fixture.Time.Now,
                    OverlapUntilUtc = fixture.Time.Now, RetiredAtUtc = fixture.Time.Now, RetiredIndex = image.Index };
                break;
            case "issuer-revoked":
                issuer = issuer with { RevokedAtUtc = fixture.Time.Now };
                break;
            case "issuer-retired":
                issuer = issuer with { RetiredAtUtc = fixture.Time.Now };
                break;
            case "issuer-expired":
                issuer = issuer with { NotAfterUtc = fixture.Time.Now };
                break;
            case "issuer-missing":
                image = image with { OnboardingContracts = image.OnboardingContracts with { Issuers = image.OnboardingContracts.Issuers!.Clear() } };
                break;
            case "issuer-owner-removed":
                OperationalNode owner = image.Operations.Nodes[issuer.NodeId];
                image = image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(owner.NodeId, owner with { Removed = true }) } };
                break;
            case "issuer-purpose":
                issuer = issuer with { Purpose = "EmergencyRecovery" };
                break;
            case "voter-removed":
                node = node with { Removed = true };
                break;
            case "nonvoter":
                node = node with { Voting = false };
                break;
            case "storage":
                node = node with { StorageGeneration = Guid.NewGuid() };
                break;
            case "address":
                node = node with { Address = "https://changed.example.test/internal/raft" };
                break;
            case "maintenance":
                image = image with { Operations = image.Operations with { MaintenanceId = Guid.NewGuid() } };
                break;
            case "membership":
                image = image with { Operations = image.Operations with { MembershipVersion = image.Operations.MembershipVersion + 1 } };
                break;
            case "process":
                process = process with { ProcessEpoch = process.ProcessEpoch + 1 };
                break;
            case "lease-expired":
                process = process with { LeaseUntilUtc = fixture.Time.Now };
                break;
            case "proof-expired":
                fixture.Time.Now = fixture.Time.Now.AddSeconds(60);
                break;
            case "proof-future":
                fixture.Time.Now = fixture.Time.Now.AddTicks(-1);
                break;
            case "authorization-index":
                rotation = rotation with { AuthorizedIndex = rotation.AuthorizedIndex + 1 };
                break;
            case "authorization-digest":
                rotation = rotation with { AuthorizationDigest = new string('F', 64) };
                break;
            case "authorization-operation":
                rotation = rotation with { AuthorizationOperationId = Guid.NewGuid() };
                break;
            case "authorization-intent":
                rotation = rotation with { Renewal = rotation.Renewal with { PreviousFingerprint = new string('F', 64) } };
                break;
            case "authorization-result":
                result = result with { Outcome = OperationOutcome.Rejected };
                break;
            case "bound":
                rotation = rotation with { CredentialRevision = image.Index };
                break;
            case "projection":
                fixture.Subject.Invalidate();
                break;
            case "compatibility":
                fixture.SubjectOptions.CommandSchema = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        if (mutation != "issuer-missing")
        {
            image = image with { OnboardingContracts = image.OnboardingContracts! with
            {
                Issuers = image.OnboardingContracts.Issuers!.SetItem(issuer.Epoch, issuer)
            } };
        }

        image = image with
        {
            Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(node.NodeId, node),
                Credentials = image.Operations.Credentials.SetItem(old.Id, old) },
            OnboardingContracts = image.OnboardingContracts! with
            {
                OnlineProcesses = image.OnboardingContracts.OnlineProcesses!.SetItem(process.NodeId, process),
                VoterCredentialRotations = image.OnboardingContracts.VoterCredentialRotations!.SetItem(fixture.Request.Renewal.Issuance.IssueId, rotation)
            },
            Results = image.Results.SetItem(result.OperationId, result)
        };
        fixture.Scenario.State = image;
        fixture.Publish();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(original.CertificateDer, Assert.Single(fixture.Journal.Issued).Value.CertificateDer);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Single(fixture.Subject.Current!.Operations.Credentials);
    }

    [Theory]
    [InlineData("caller-majority")]
    [InlineData("revocation")]
    [InlineData("membership")]
    [InlineData("recovery")]
    public async Task CallerRechecksAfterRemoteSignerReturns(string boundary)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        fixture.Remote.After = () =>
        {
            RevisionImage image = fixture.Scenario.State;
            if (boundary == "caller-majority")
            {
                fixture.SubjectBarrier.Available = false;
            }
            else if (boundary == "revocation")
            {
                OperationalCredential old = image.Operations.Credentials[fixture.Request.Renewal.PreviousCredentialId];
                image = image with { Operations = image.Operations with { Credentials = image.Operations.Credentials.SetItem(old.Id,
                    old with { RevokedAtUtc = fixture.Time.Now }) } };
            }
            else if (boundary == "membership")
            {
                image = image with { Operations = image.Operations with { MembershipVersion = image.Operations.MembershipVersion + 1 } };
            }
            else
            {
                fixture.Subject.Invalidate();
            }

            fixture.Subject.Publish(image);
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.Single(fixture.Journal.Issued);
        Assert.Single(fixture.Subject.Current!.Operations.Credentials);
    }
}
