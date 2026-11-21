using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterContinuationBindingTests
{
    [Fact]
    public async Task LostBindResultSurvivesWalRecoveryAndSnapshotWithSameRevision()
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        RevisionImage initial = scenario.State;
        (string registration, EnrollmentIssuedArtifact artifact) = await IssueAsync(fixture);
        string certificate = artifact.CertificateDigest;
        VoterCredentialProofRequest call = await fixture.BindProofAsync(registration, certificate);
        VoterCredentialProofRequest issueAgain = fixture.Prove(await fixture.ChallengeAsync(registration));
        scenario.LoseResponse = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.Binding().BindAsync(call, registration, certificate, scenario.Token));

        // Assert
        RevisionCommand bind = RevisionCodec.Decode(scenario.Wal.Last());
        CommandResult committed = scenario.State.Results[bind.OperationId];
        VoterCredentialRotation rotation = scenario.State.OnboardingContracts!.VoterCredentialRotations![artifact.IssueId];
        Assert.Equal(OperationOutcome.Committed, committed.Outcome);
        Assert.Equal(committed.Index, rotation.CredentialRevision);
        Assert.Equal(bind.OperationId, rotation.BindOperationId);
        Assert.Equal(call.Request.OperationId, rotation.ContinuationRequestId);
        Assert.Equal(Convert.ToBase64String(artifact.CertificateDer), scenario.State.Operations.Credentials[artifact.IssueId].PublicCertificate);
        Assert.Equal(initial.Operations.Credentials[rotation.Renewal.PreviousCredentialId].ExpiresAtUtc, rotation.OverlapUntilUtc);
        Assert.Same(initial.Operations.Nodes, scenario.State.Operations.Nodes);
        Assert.Equal(initial.Operations.MembershipVersion, scenario.State.Operations.MembershipVersion);
        Assert.Equal(4, scenario.Wal.Count);
        Assert.Equal(1, fixture.Journal.Commits);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Dispatcher.AcceptAsync(bind, bind.OriginNodeId, scenario.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service().IssueAsync(issueAgain, registration, scenario.Token));

        // Arrange
        scenario.LoseResponse = false;
        RevisionStateStore restored = new();
        restored.Publish(initial);
        restored.BeginRecovery();
        RevisionRecoveryTests.Projection projection = new(initial);
        RevisionApplyEngine engine = new(restored, new RevisionReducer(), projection);

        // Act
        foreach (byte[] payload in scenario.Wal)
        {
            RevisionCommand command = RevisionCodec.Decode(payload);
            await engine.ApplyAsync(command, restored.Current!.Index + 1, scenario.Term, scenario.Options.NodeId, scenario.Token);
        }

        Assert.False(restored.Available);
        bool recovered = await engine.ConfirmRecoveryAsync(async image =>
        {
            await projection.VerifyAsync(image, scenario.Token);
            return true;
        }, scenario.Token);

        // Assert
        Assert.True(recovered);
        Assert.Equal(4, projection.Effects);
        Assert.Equal(committed, restored.Current!.Results[bind.OperationId]);

        // Arrange
        scenario.Options.StoragePath = fixture.Installation.Cluster.StoragePath;
        RevisionSnapshotStore snapshots = new(Options.Create(scenario.Options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(restored.Current, scenario.Token);

        // Act
        PublishedRevisionSnapshot loaded = await snapshots.LoadAsync(snapshot.Manifest.SnapshotId, snapshot.Digest, scenario.Token);
        scenario.Crypto.State = loaded.Image;
        RevisionImage archived = scenario.Crypto.Archived();
        OnboardingSnapshot.Validate(archived);
        VoterCredentialArtifacts.ValidateSnapshot(archived);
        scenario.Store.Publish(loaded.Image);
        VoterContinuationAuthority restarted = fixture.NewAuthority();
        VoterContinuationBind service = fixture.Binding(restarted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BindAsync(call, registration, certificate, scenario.Token));
        VoterCredentialProofRequest retry = await fixture.BindProofAsync(registration, certificate, restarted);
        CommandResult replay = await service.BindAsync(retry, registration, certificate, scenario.Token);

        // Assert
        Assert.Equal(committed, replay);
        Assert.Equal(rotation, loaded.Image.OnboardingContracts!.VoterCredentialRotations![artifact.IssueId]);
        Assert.Equal(bind, loaded.Image.OnboardingContracts.VoterContinuations![call.Request.OperationId].Bind);
        Assert.Null(archived.OnboardingContracts);
        Assert.Throws<InvalidDataException>(() => OnboardingSnapshot.Validate(loaded.Image with
        {
            OnboardingContracts = loaded.Image.OnboardingContracts with { VoterCredentialRotations = null }
        }));
        Assert.Throws<InvalidDataException>(() => OnboardingSnapshot.Validate(archived with
        {
            Results = archived.Results.SetItem(committed.OperationId, committed with { Digest = new string('D', 64) })
        }));
        Assert.Equal(4, scenario.Wal.Count);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.False(VoterCredentialPolicy.IsUsable(loaded.Image,
            initial.Operations.Credentials[rotation.Renewal.PreviousCredentialId], scenario.Time.Now));
    }

    [Fact]
    public async Task ExactBindReplayRejectsRevokedNewCredential()
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        (string registration, EnrollmentIssuedArtifact artifact) = await IssueAsync(fixture);
        string certificate = artifact.CertificateDigest;
        VoterContinuationBind service = fixture.Binding();
        CommandResult first = await service.BindAsync(await fixture.BindProofAsync(registration, certificate),
            registration, certificate, scenario.Token);
        Assert.Equal(OperationOutcome.Committed, first.Outcome);
        VoterCredentialProofRequest retry = await fixture.BindProofAsync(registration, certificate);
        RevisionImage image = scenario.State;
        OperationalCredential bound = image.Operations.Credentials[artifact.IssueId];
        scenario.Store.Publish(image with
        {
            Operations = image.Operations with
            {
                Credentials = image.Operations.Credentials.SetItem(bound.Id, bound with { RevokedAtUtc = scenario.Time.Now })
            }
        });

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BindAsync(retry, registration, certificate, scenario.Token));

        // Assert
        Assert.Equal(first, scenario.State.Results[first.OperationId]);
        Assert.Equal(4, scenario.Wal.Count);
        Assert.Equal(1, fixture.Journal.Commits);
        OnboardingSnapshot.Validate(scenario.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditUsesActualAcceptedOutcomeAndKeepsCredentialMaterialOutOfLogs(bool removeRoleAtApply)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        (string registration, EnrollmentIssuedArtifact artifact) = await IssueAsync(fixture);
        VoterCredentialProofRequest first = await fixture.BindProofAsync(registration, artifact.CertificateDigest);
        if (removeRoleAtApply)
        {
            scenario.BeforeApply = () => scenario.ChangeActor(enabled: false);
        }

        // Act
        CommandResult decision = await fixture.Binding().BindAsync(first, registration, artifact.CertificateDigest, scenario.Token);

        // Assert
        if (removeRoleAtApply)
        {
            Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
            Assert.False(scenario.State.Operations.Credentials.ContainsKey(artifact.IssueId));
            Assert.DoesNotContain(fixture.Logs.Messages, item => item.Message.Contains("CertificateBound", StringComparison.Ordinal));
            Assert.Single(fixture.Logs.Messages, item => item.Level == LogLevel.Warning
                && item.Message.Contains("CertificateRejected by continuation", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(OperationOutcome.Committed, decision.Outcome);
            VoterCredentialProofRequest fresh = await fixture.BindProofAsync(registration, artifact.CertificateDigest);
            Assert.Equal(decision, await fixture.Binding().BindAsync(fresh, registration, artifact.CertificateDigest, scenario.Token));
            Assert.Single(fixture.Logs.Messages, item => item.Level == LogLevel.Information
                && item.Message.Contains("CertificateBound by continuation", StringComparison.Ordinal)
                && item.Message.Contains(scenario.Actor.ToString(), StringComparison.Ordinal));
        }

        Assert.Equal(4, scenario.Wal.Count);
        Assert.All(fixture.Logs.Messages, entry =>
        {
            Assert.DoesNotContain(first.CsrDer, entry.Message);
            Assert.DoesNotContain(first.Proof.Nonce, entry.Message);
            Assert.DoesNotContain(first.Proof.Signature, entry.Message);
            Assert.DoesNotContain(Convert.ToBase64String(artifact.CertificateDer), entry.Message);
        });
    }

    [Theory]
    [InlineData("no-issue")]
    [InlineData("der")]
    [InlineData("journal-registration")]
    [InlineData("certificate-digest")]
    [InlineData("registration")]
    [InlineData("csr")]
    [InlineData("operation")]
    [InlineData("issue")]
    [InlineData("storage")]
    [InlineData("boot")]
    [InlineData("lease")]
    [InlineData("role")]
    [InlineData("voter")]
    [InlineData("previous-revoked")]
    [InlineData("issuer-revoked")]
    [InlineData("term")]
    [InlineData("majority-before-write")]
    [InlineData("issue-nonce")]
    public async Task BindFailsClosedWhenIssuedArtifactOrCurrentAuthorityIsMissing(string change)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        (string registration, EnrollmentIssuedArtifact artifact) = await IssueAsync(fixture);
        string certificate = artifact.CertificateDigest;
        VoterCredentialProofRequest call = await fixture.BindProofAsync(registration, certificate);
        RevisionImage image = scenario.State;
        EnrollmentIssuance leaf = call.Request.Renewal.Issuance;
        switch (change)
        {
            case "no-issue":
                fixture.Journal.Issued.Remove(artifact.IssueId);
                break;
            case "der":
                fixture.Journal.Issued[artifact.IssueId] = artifact with { CertificateDer = [1, 2, 3] };
                break;
            case "journal-registration":
                fixture.Journal.Issued[artifact.IssueId] = artifact with { AuthorizationIndex = artifact.AuthorizationIndex - 1 };
                break;
            case "certificate-digest":
                certificate = new string('A', 64);
                break;
            case "registration":
                registration = new string('B', 64);
                break;
            case "csr":
                call = call with { CsrDer = Convert.ToBase64String([1, 2, 3]) };
                break;
            case "operation":
                call = fixture.Prove(call.Proof.Nonce, call.Request with { OperationId = Guid.NewGuid() });
                break;
            case "issue":
                call = fixture.Prove(call.Proof.Nonce, call.Request with
                {
                    Renewal = call.Request.Renewal with { Issuance = leaf with { IssueId = Guid.NewGuid() } }
                });
                break;
            case "storage":
                ChangeNode(image.Operations.Nodes[leaf.NodeId] with { StorageGeneration = Guid.NewGuid() });
                break;
            case "boot":
                call = fixture.Prove(call.Proof.Nonce, candidate: call.Proof with { BootId = Guid.NewGuid() });
                break;
            case "lease":
                scenario.Time.Now = image.OnboardingContracts!.OnlineProcesses![leaf.NodeId].LeaseUntilUtc;
                break;
            case "role":
                scenario.ChangeActor(enabled: false);
                break;
            case "voter":
                ChangeNode(image.Operations.Nodes[leaf.NodeId] with { Removed = true });
                break;
            case "previous-revoked":
                OperationalCredential previous = image.Operations.Credentials[call.Request.Renewal.PreviousCredentialId];
                scenario.Store.Publish(image with
                {
                    Operations = image.Operations with
                    {
                        Credentials = image.Operations.Credentials.SetItem(previous.Id, previous with { RevokedAtUtc = scenario.Time.Now })
                    }
                });
                break;
            case "issuer-revoked":
                fixture.PublishIssuer(image.OnboardingContracts!.Issuers![leaf.IssuerEpoch] with { RevokedAtUtc = scenario.Time.Now });
                break;
            case "term":
                scenario.Term++;
                break;
            case "majority-before-write":
                int beforeWrite = scenario.Barrier.Calls + 2;
                scenario.Barrier.After = count =>
                {
                    if (count == beforeWrite)
                    {
                        throw new InvalidOperationException("EnrollmentReadMajorityUnavailable");
                    }
                };
                break;
            case "issue-nonce":
                call = fixture.Prove(await fixture.ChallengeAsync(registration));
                break;
        }

        // Act
        Exception? refused = await Record.ExceptionAsync(() => fixture.Binding().BindAsync(call, registration, certificate, scenario.Token));

        // Assert
        Assert.NotNull(refused);
        Assert.Equal(3, scenario.Wal.Count);
        Assert.False(scenario.State.Operations.Credentials.ContainsKey(artifact.IssueId));
        Assert.DoesNotContain(fixture.Logs.Messages, item => item.Message.Contains("CertificateBound", StringComparison.Ordinal));
        Assert.Single(fixture.Logs.Messages, item => item.Level == LogLevel.Warning
            && item.Message.Contains("Bind denied or unconfirmed", StringComparison.Ordinal));

        void ChangeNode(OperationalNode node)
        {
            scenario.Store.Publish(image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(node.NodeId, node) } });
        }
    }

    internal static async Task<(string Registration, EnrollmentIssuedArtifact Artifact)> IssueAsync(VoterContinuationSigningFixture fixture)
    {
        string registration = (await fixture.RegisterAsync()).Digest;
        VoterCredentialProofRequest proof = fixture.Prove(await fixture.ChallengeAsync(registration));
        EnrollmentIssuedArtifact artifact = await fixture.Service().IssueAsync(proof, registration, fixture.Scenario.Token);
        return (registration, artifact);
    }
}
