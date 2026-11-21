using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterContinuationSigningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResultAndWalReplayReturnExactlyOnePersistedDer(bool loseCommitResponse)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        RevisionImage initial = scenario.State;
        CommandResult registration = await fixture.RegisterAsync();
        string digest = registration.Digest;
        VoterCredentialProofRequest first = fixture.Prove(await fixture.ChallengeAsync(digest));
        fixture.Journal.LoseCommitResponse = loseCommitResponse;

        // Act
        if (loseCommitResponse)
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Service().IssueAsync(first, digest, scenario.Token));
        }
        else
        {
            await fixture.Service().IssueAsync(first, digest, scenario.Token); // Ответ потерян после сохранения.
        }

        // Assert
        EnrollmentIssuedArtifact persisted = Assert.Single(fixture.Journal.Issued).Value;
        Assert.Equal(scenario.Request.OperationId, persisted.AttemptId);
        Assert.Equal(scenario.Request.Renewal.Issuance.IssueId, persisted.IssueId);
        Assert.Equal(registration.Index, persisted.AuthorizationIndex);
        Assert.NotEqual(VoterCredentialProofs.Digest(scenario.Request), persisted.RequestDigest);

        // Arrange
        fixture.Journal.LoseCommitResponse = false;
        RevisionStateStore replayStore = new();
        replayStore.Publish(initial);
        replayStore.BeginRecovery();
        RevisionRecoveryTests.Projection projection = new(initial);
        RevisionApplyEngine engine = new(replayStore, new RevisionReducer(), projection);

        // Act
        foreach (byte[] entry in scenario.Wal)
        {
            RevisionCommand command = RevisionCodec.Decode(entry);
            await engine.ApplyAsync(command, replayStore.Current!.Index + 1, scenario.Term, scenario.Options.NodeId, scenario.Token);
        }

        bool replayConfirmed = await engine.ConfirmRecoveryAsync(async image =>
        {
            await projection.VerifyAsync(image, scenario.Token);
            return true;
        }, scenario.Token);

        // Assert
        Assert.True(replayConfirmed);
        Assert.Equal(3, projection.Effects);
        RevisionImage replay = replayStore.Current!;
        Assert.Equal(registration, replay.Results[registration.OperationId]);

        // Arrange
        scenario.Store.Publish(replay);
        VoterContinuationAuthority restarted = fixture.NewAuthority();
        VoterContinuationIssuance service = fixture.Service(restarted);

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.IssueAsync(first, digest, scenario.Token));
        VoterCredentialProofRequest retry = fixture.Prove(await fixture.ChallengeAsync(digest, restarted));
        EnrollmentIssuedArtifact result = await service.IssueAsync(retry, digest, scenario.Token);

        // Assert
        Assert.Equal(persisted.CertificateDer, result.CertificateDer);
        Assert.Equal(persisted.CertificateDigest, result.CertificateDigest);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Equal(3, scenario.Wal.Count);
        Assert.Same(initial.Operations, scenario.State.Operations);
        Assert.Null(scenario.State.OnboardingContracts!.VoterCredentialRotations);
    }

    [Fact]
    public async Task ApprovalDigestAloneCannotAuthorizeIssuanceNonce()
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        CommandResult approval = await scenario.ApproveAsync(await scenario.ProposeAsync());

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.ChallengeAsync(approval.Digest));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Empty(fixture.Journal.Issued);
        Assert.Equal(2, scenario.Wal.Count);
    }

    [Fact]
    public async Task ConcurrentExactCallsAndCompetingBootCannotCreateAnotherResult()
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        string digest = (await fixture.RegisterAsync()).Digest;
        VoterCredentialProofRequest first = fixture.Prove(await fixture.ChallengeAsync(digest));
        VoterCredentialProofRequest second = fixture.Prove(await fixture.ChallengeAsync(digest));
        VoterCredentialProofRequest other = fixture.Prove(await fixture.ChallengeAsync(digest),
            candidate: scenario.Candidate with { BootId = Guid.NewGuid(), ProcessEpoch = scenario.Candidate.ProcessEpoch + 1 });
        TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = scenario.Barrier.Calls + 1;
        scenario.Barrier.Wait = async call =>
        {
            if (call == next)
            {
                locked.SetResult();
                await release.Task.WaitAsync(scenario.Token);
            }
        };

        // Act
        Task<EnrollmentIssuedArtifact> queued = fixture.Service().IssueAsync(first, digest, scenario.Token);
        await locked.Task.WaitAsync(scenario.Token);
        Task<EnrollmentIssuedArtifact> competing = fixture.Service().IssueAsync(second, digest, scenario.Token);
        release.SetResult();
        EnrollmentIssuedArtifact[] results = await Task.WhenAll(queued, competing);
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Service().IssueAsync(other, digest, scenario.Token));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(rejected);
        Assert.Equal(results[0].CertificateDer, results[1].CertificateDer);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Single(fixture.Logs.Messages, message => message.Level == LogLevel.Information);
        Assert.Contains(fixture.Logs.Messages, message => message.Level == LogLevel.Warning);
        Assert.All(fixture.Logs.Messages, message =>
        {
            Assert.DoesNotContain(first.Proof.Nonce, message.Message);
            Assert.DoesNotContain(first.CsrDer, message.Message);
            Assert.DoesNotContain(Convert.ToBase64String(results[0].CertificateDer), message.Message);
        });

        Assert.ThrowsAny<IOException>(() => new InstallationLease(fixture.Installation.Options, fixture.Installation.Cluster.StoragePath));
    }

    [Fact]
    public async Task AfterCommitTheSignerChecksLatestAuthorityBeforeReleasingDer()
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        string digest = (await fixture.RegisterAsync()).Digest;
        VoterCredentialProofRequest first = fixture.Prove(await fixture.ChallengeAsync(digest));
        int afterCommit = scenario.Barrier.Calls + 2;
        scenario.Barrier.After = call =>
        {
            if (call == afterCommit)
            {
                Assert.Equal(1, fixture.Journal.Commits);
                scenario.ChangeActor(enabled: false);
            }
        };

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Service().IssueAsync(first, digest, scenario.Token));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(error);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ChallengeAsync(digest));
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Level == LogLevel.Information);
        Assert.Equal(1, fixture.Journal.Commits);
    }

    [Theory]
    [InlineData("csr")]
    [InlineData("intent")]
    [InlineData("revision")]
    [InlineData("revoked")]
    [InlineData("issuer-revoked")]
    [InlineData("administrator-removed")]
    [InlineData("lease")]
    [InlineData("term")]
    [InlineData("majority")]
    [InlineData("registration")]
    [InlineData("status-nonce")]
    public async Task StoredDerStillRequiresLiveExactAuthority(string change)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        string digest = (await fixture.RegisterAsync()).Digest;
        VoterContinuationIssuance service = fixture.Service();
        await service.IssueAsync(fixture.Prove(await fixture.ChallengeAsync(digest)), digest, scenario.Token);
        VoterCredentialProofRequest retry = fixture.Prove(await fixture.ChallengeAsync(digest));
        RevisionImage image = scenario.State;
        switch (change)
        {
            case "csr":
                using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    byte[] other = new CertificateRequest("CN=other", key, HashAlgorithmName.SHA256).CreateSigningRequest();
                    retry = retry with { CsrDer = Convert.ToBase64String(other) };
                }
                break;
            case "intent":
                VoterCredentialRequest altered = scenario.Request with
                {
                    Renewal = scenario.Request.Renewal with
                    {
                        Issuance = scenario.Request.Renewal.Issuance with { IssueId = Guid.NewGuid() }
                    }
                };
                retry = fixture.Prove(retry.Proof.Nonce, altered);
                break;
            case "revision":
                retry = fixture.Prove(retry.Proof.Nonce, scenario.Request with
                {
                    Renewal = scenario.Request.Renewal with { PreviousCredentialRevision = 1 }
                });
                break;
            case "revoked":
                OperationalCredential old = image.Operations.Credentials[scenario.Request.Renewal.PreviousCredentialId];
                scenario.Store.Publish(image with
                {
                    Operations = image.Operations with
                    {
                        Credentials = image.Operations.Credentials.SetItem(old.Id, old with { RevokedAtUtc = scenario.Time.Now })
                    }
                });
                break;
            case "issuer-revoked":
                AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![scenario.Request.Renewal.Issuance.IssuerEpoch];
                fixture.PublishIssuer(issuer with { RevokedAtUtc = scenario.Time.Now, Revision = issuer.Revision + 1 });
                break;
            case "administrator-removed":
                scenario.Store.Publish(image with { Values = image.Values.Remove(scenario.Actor) });
                break;
            case "lease":
                scenario.Time.Now = image.OnboardingContracts!.OnlineProcesses![scenario.Candidate.OriginNodeId].LeaseUntilUtc;
                retry = fixture.Prove(retry.Proof.Nonce);
                break;
            case "term":
                scenario.Term++;
                break;
            case "majority":
                scenario.Barrier.Available = false;
                break;
            case "registration":
                digest = new string('A', 64);
                break;
            case "status-nonce":
                VoterCredentialProofRequest status = fixture.Prove(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                status = status with { Proof = VoterCredentialContinuationProofs.SignChallenge(status.Request, status.Proof,
                    scenario.Crypto.Crypto.PeerKey, VoterContinuationAuthority.STATUS, scenario.Options.PublicUri, null) };
                retry = fixture.Prove(await scenario.Authority.IssueChallengeAsync(status, VoterContinuationAuthority.STATUS, null, scenario.Token));
                break;
        }

        // Act
        Exception? error = await Record.ExceptionAsync(() => service.IssueAsync(retry, digest, scenario.Token));

        // Assert
        if (change == "majority")
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        else if (change == "csr")
        {
            Assert.Equal("EnrollmentCsrDigestInvalid", Assert.IsType<InvalidDataException>(error).Message);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
        }

        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Equal(3, scenario.Wal.Count);
    }
}
