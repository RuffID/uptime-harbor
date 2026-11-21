using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialSigningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAndRemoteSignerPersistExactDerWithoutBindingOrChangingMembership(bool remote)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(remote);
        await fixture.AuthorizeAsync();
        RevisionImage before = fixture.Subject.Current!;
        int subjectBarriers = fixture.SubjectBarrier.Calls;
        int issuerBarriers = fixture.IssuerBarrier.Calls;

        // Act
        EnrollmentIssuedArtifact issued = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);
        EnrollmentIssuedArtifact replay = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(issued.CertificateDer, replay.CertificateDer);
        Assert.Equal(issued.IssueId, replay.IssueId);
        Assert.Equal(fixture.Request.OperationId, issued.AttemptId);
        Assert.Equal(fixture.Request.Renewal.Issuance.IssueId, issued.IssueId);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.True(fixture.SubjectBarrier.Calls - subjectBarriers >= 4);
        Assert.True(fixture.IssuerBarrier.Calls - issuerBarriers >= 4);
        Assert.Same(before, fixture.Subject.Current);
        Assert.Single(before.Operations.Credentials);
        Assert.Equal(7, before.Operations.MembershipVersion);
        Assert.Equal(0, Assert.Single(before.OnboardingContracts!.VoterCredentialRotations!).Value.CredentialRevision);
        Assert.Null(before.Onboarding);
        Assert.Single(fixture.Logs.Messages, item => item.Level == LogLevel.Information);
        string log = string.Join('\n', fixture.Logs.Messages.Select(item => item.Message));
        Assert.DoesNotContain(Convert.ToBase64String(issued.CertificateDer), log);
        Assert.DoesNotContain(fixture.Call.CsrDer, log);
        Assert.DoesNotContain(fixture.Call.Proof.Signature, log);
        Assert.DoesNotContain("PRIVATE KEY", log);
    }

    [Theory]
    [InlineData("issuer-outage")]
    [InlineData("caller-majority")]
    [InlineData("issuer-majority")]
    [InlineData("missing-key")]
    [InlineData("wrong-key")]
    [InlineData("installation")]
    [InlineData("epoch")]
    [InlineData("owner")]
    [InlineData("commit")]
    public async Task OutageOrWrongIssuerNeverFallsBackOrReturnsNewIdentity(string mutation)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        EnrollmentIssuerReference local = Assert.Single(fixture.Journal.Issuers).Value;
        switch (mutation)
        {
            case "issuer-outage":
                fixture.Remote.Available = false;
                break;
            case "caller-majority":
                fixture.SubjectBarrier.Available = false;
                break;
            case "issuer-majority":
                fixture.IssuerBarrier.Available = false;
                break;
            case "missing-key":
                fixture.Journal.Issuers.Clear();
                break;
            case "wrong-key":
                fixture.Journal.Issuers[local.Epoch] = local with { SpkiDigest = new string('F', 64) };
                break;
            case "installation":
                fixture.Journal.Issuers[local.Epoch] = local with { InstallIdentity = Guid.NewGuid() };
                break;
            case "epoch":
                fixture.Journal.Issuers[local.Epoch] = local with { Epoch = Guid.NewGuid() };
                break;
            case "owner":
                fixture.Journal.Issuers[local.Epoch] = local with { NodeId = fixture.SubjectId };
                break;
            case "commit":
                fixture.Journal.FailBeforeCommit = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.Empty(fixture.Journal.Issued);
        Assert.Single(fixture.Subject.Current!.Operations.Credentials);
        Assert.DoesNotContain(fixture.Logs.Messages, item => item.Level == LogLevel.Information);
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("epoch")]
    [InlineData("operation")]
    [InlineData("digest")]
    [InlineData("template")]
    [InlineData("authorization")]
    [InlineData("genesis")]
    [InlineData("der")]
    [InlineData("fingerprint")]
    [InlineData("approved-root-other-leaf")]
    public async Task CallerRejectsAlteredRemoteResponseAfterFreshBarrier(string mutation)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        fixture.Remote.Alter = artifact =>
        {
            if (mutation == "approved-root-other-leaf")
            {
                return artifact with { CertificateDer = fixture.Scenario.Previous.RawData,
                    CertificateDigest = UptimeHarbor.Infrastructure.Cluster.Onboarding.EnrollmentCertificates.Hash(fixture.Scenario.Previous.RawData) };
            }

            return Alter(artifact, mutation);
        };
        int barriers = fixture.SubjectBarrier.Calls;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.True(fixture.SubjectBarrier.Calls - barriers >= 2);
        Assert.Single(fixture.Journal.Issued);
        Assert.Single(fixture.Subject.Current!.Operations.Credentials);
        Assert.Equal(0, Assert.Single(fixture.Subject.Current.OnboardingContracts!.VoterCredentialRotations!).Value.CredentialRevision);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("commit-response")]
    [InlineData("post-sign-majority")]
    public async Task LostResponseAndLeaderChangeReplayOriginalIntentAndDer(string boundary)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        fixture.Remote.LoseResponse = boundary == "transport";
        fixture.Journal.LoseCommitResponse = boundary == "commit-response";
        int calls = fixture.IssuerBarrier.Calls;
        if (boundary == "post-sign-majority")
        {
            fixture.IssuerBarrier.After = count =>
            {
                if (count == calls + 2)
                {
                    throw new InvalidOperationException("SimulatedMajorityLostAfterCommit");
                }
            };
        }

        // Act
        Exception? unknown = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));
        EnrollmentIssuedArtifact durable = Assert.Single(fixture.Journal.Issued).Value;
        fixture.Remote.LoseResponse = false;
        fixture.Journal.LoseCommitResponse = false;
        fixture.IssuerBarrier.After = null;
        fixture.Scenario.State = fixture.Scenario.State with { Term = fixture.Scenario.State.Term + 1, Index = fixture.Scenario.State.Index + 1 };
        fixture.Publish();
        RevisionCommand retried = await fixture.PrepareAsync();
        EnrollmentIssuedArtifact replay = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(unknown);
        Assert.Equal(durable.CertificateDer, replay.CertificateDer);
        Assert.Equal(durable.TemplateDigest, replay.TemplateDigest);
        Assert.Equal(durable.RequestDigest, replay.RequestDigest);
        Assert.Equal(durable.IssueId, replay.IssueId);
        Assert.Equal(fixture.Request.OperationId, retried.OperationId);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Single(fixture.Subject.Current!.Operations.Credentials);
    }

    [Fact]
    public async Task ConcurrentScopesShareIssuerLockAndReturnOneDurableDer()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int initialBarriers = fixture.IssuerBarrier.Calls;
        fixture.IssuerBarrier.Wait = count =>
        {
            if (count == initialBarriers + 1)
            {
                entered.SetResult();
                return new ValueTask(release.Task);
            }

            return ValueTask.CompletedTask;
        };

        // Act
        Task<EnrollmentIssuedArtifact> first = fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<EnrollmentIssuedArtifact>[] competing = Enumerable.Range(0, 7)
            .Select(_ => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken)).ToArray();
        bool blocked = competing.All(task => !task.IsCompleted);
        release.SetResult();
        EnrollmentIssuedArtifact[] results = await Task.WhenAll(competing.Prepend(first));

        // Assert
        Assert.All(results, item => Assert.Equal(results[0].CertificateDer, item.CertificateDer));
        Assert.True(blocked);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Single(fixture.Journal.Issued);
        Assert.Single(fixture.Logs.Messages, item => item.Level == LogLevel.Information);
    }

    private static EnrollmentIssuedArtifact Alter(EnrollmentIssuedArtifact artifact, string mutation)
    {
        switch (mutation)
        {
            case "issue":
                return artifact with { IssueId = Guid.NewGuid() };
            case "epoch":
                return artifact with { IssuerEpoch = Guid.NewGuid() };
            case "operation":
                return artifact with { AttemptId = Guid.NewGuid() };
            case "digest":
                return artifact with { RequestDigest = new string('F', 64) };
            case "template":
                return artifact with { TemplateDigest = new string('F', 64) };
            case "authorization":
                return artifact with { AuthorizationIndex = artifact.AuthorizationIndex + 1 };
            case "genesis":
                return artifact with { GenesisId = Guid.NewGuid() };
            case "der":
                return artifact with { CertificateDer = [0, 1, 2] };
            case "fingerprint":
                return artifact with { CertificateDigest = new string('F', 64) };
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }
}
