using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterContinuationBoundRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationAfterExpiryPreservesOriginalBindAcrossLostResponseAndReplay(bool newTerm)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        RevisionImage initial = scenario.State;
        var (context, bound, oldLease) = await BindAsync(fixture);
        scenario.Time.Now = oldLease.LeaseUntilUtc;
        if (newTerm)
        {
            scenario.Term++;
        }

        CommandResult request = await scenario.Authority.RequestAsync(
            await ProveAsync(fixture, context, VoterContinuationAuthority.REQUEST), scenario.Token);
        var status = await scenario.Authority.ReadAdministratorStatusAsync(context.Request.OperationId, scenario.Token);
        Assert.Equal(request.Digest, status.RequestDigest);
        CommandResult approval = await scenario.Authority.AuthorizeAsync(context.Request.OperationId, request.Digest, "test-password", scenario.Token);
        VoterCredentialProofRequest register = await ProveAsync(fixture, context, VoterContinuationAuthority.REGISTER, approval.Digest);
        scenario.LoseResponse = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.Authority.RegisterAsync(register, approval.Digest, scenario.Token));
        RevisionImage replay = initial;
        foreach (byte[] bytes in scenario.Wal)
        {
            RevisionCommand entry = RevisionCodec.Decode(bytes);
            replay = new RevisionReducer().Apply(replay, entry, replay.Index + 1, entry.VoterContinuation!.Acceptance!.Term);
        }

        scenario.Options.StoragePath = fixture.Installation.Cluster.StoragePath;
        RevisionSnapshotStore snapshots = new(Options.Create(scenario.Options));
        var snapshot = await snapshots.PublishAsync(replay, scenario.Token);
        var loaded = await snapshots.LoadAsync(snapshot.Manifest.SnapshotId, snapshot.Digest, scenario.Token);
        scenario.Store.Publish(loaded.Image);
        scenario.LoseResponse = false;
        scenario.Time.Now = scenario.Time.Now.AddSeconds(1);
        CommandResult retry = await scenario.Authority.RegisterAsync(
            await ProveAsync(fixture, context, VoterContinuationAuthority.REGISTER, approval.Digest), approval.Digest, scenario.Token);

        // Assert
        var contracts = loaded.Image.OnboardingContracts!;
        var resumed = contracts.VoterContinuations![context.Request.OperationId];
        var original = contracts.VoterContinuations[context.BoundSource!.RequestId];
        var leaf = context.Request.Renewal.Issuance;
        var current = contracts.OnlineProcesses![leaf.NodeId];
        Assert.Equal(OperationOutcome.Committed, retry.Outcome);
        Assert.Equal(retry.OperationId, current.GrantId);
        Assert.True(current.CredentialContinuation);
        Assert.Equal(context.Process.BootId, current.BootId);
        Assert.Equal(context.Process.BootChallenge, current.BootChallenge);
        Assert.Equal(oldLease.ProcessEpoch + 1, current.ProcessEpoch);
        Assert.Equal(oldLease.LeaseUntilUtc.AddMinutes(2), current.LeaseUntilUtc);
        Assert.Equal(oldLease, resumed.Registration!.VoterContinuation!.Context.Predecessor);
        Assert.Equal(oldLease.Index, loaded.Image.Results[original.Registration!.OperationId].Index);
        Assert.Null(resumed.Bind);
        Assert.Equal(bound, loaded.Image.Results[original.Bind!.OperationId]);
        Assert.Equal(bound.Index, Assert.Single(contracts.VoterCredentialRotations!.Values).CredentialRevision);
        Assert.Equal(original.Bind.VoterContinuation!.Credential, loaded.Image.Operations.Credentials[leaf.IssueId]);
        Assert.Equal(1, fixture.Journal.Commits);
        Assert.Equal(7, scenario.Wal.Count);
        RevisionCommand registration = resumed.Registration;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scenario.Dispatcher.AcceptAsync(registration, registration.OriginNodeId, scenario.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ProveAsync(fixture, context, VoterContinuationAuthority.ISSUE, retry.Digest));
        scenario.Time.Now = current.LeaseUntilUtc;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ProveAsync(fixture, context, VoterContinuationAuthority.REGISTER, approval.Digest));
        Assert.Equal(7, scenario.Wal.Count);
        Assert.Throws<InvalidDataException>(() => OnboardingSnapshot.Validate(loaded.Image with
        {
            OnboardingContracts = contracts with
            {
                VoterContinuations = contracts.VoterContinuations.Remove(context.BoundSource.RequestId)
            }
        }));
    }

    [Theory]
    [InlineData("live-lease", "VoterCredentialContinuationProcessUnavailable")]
    [InlineData("conflicting-boot", "VoterContinuationBoundSourceChanged")]
    [InlineData("wrong-epoch", "VoterCredentialContinuationProcessUnavailable")]
    [InlineData("wrong-bind", "VoterContinuationBoundSourceChanged")]
    [InlineData("wrong-revision", "VoterContinuationBoundSourceChanged")]
    [InlineData("identity", null)]
    [InlineData("revoked-previous", "VoterCredentialContinuationLeafUnavailable")]
    [InlineData("revoked-bound", "VoterContinuationBoundSourceChanged")]
    public async Task RefusedRequestPreservesBindAndProcess(string change, string? reason)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        VoterContinuationScenario scenario = fixture.Scenario;
        var (context, bound, oldLease) = await BindAsync(fixture);
        scenario.Time.Now = oldLease.LeaseUntilUtc;
        var leaf = context.Request.Renewal.Issuance;
        var image = scenario.State;
        switch (change)
        {
            case "live-lease":
                scenario.Time.Now = oldLease.LeaseUntilUtc.AddTicks(-1);
                break;
            case "conflicting-boot":
                var competing = oldLease with { BootId = Guid.NewGuid() };
                scenario.Store.Publish(image with { OnboardingContracts = image.OnboardingContracts! with
                    { OnlineProcesses = image.OnboardingContracts.OnlineProcesses!.SetItem(leaf.NodeId, competing) } });
                break;
            case "wrong-epoch":
                context = context with { Process = context.Process with { Epoch = context.Process.Epoch + 1 } };
                break;
            case "wrong-bind":
                context = ReplaceSource(context, context.BoundSource! with { BindDigest = new string('A', 64) });
                break;
            case "wrong-revision":
                context = ReplaceSource(context, context.BoundSource! with { CredentialRevision = bound.Index + 1 });
                break;
            case "identity":
                var node = image.Operations.Nodes[leaf.NodeId] with { StorageGeneration = Guid.NewGuid() };
                scenario.Store.Publish(image with { Operations = image.Operations with
                    { Nodes = image.Operations.Nodes.SetItem(leaf.NodeId, node) } });
                break;
            case "revoked-previous":
                Revoke(context.Request.Renewal.PreviousCredentialId);
                break;
            case "revoked-bound":
                Revoke(leaf.IssueId);
                break;
        }

        // Act
        var before = scenario.State.OnboardingContracts!;
        var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ProveAsync(fixture, context, VoterContinuationAuthority.REQUEST));

        // Assert
        if (reason is not null)
        {
            Assert.Equal(reason, refusal.Message);
        }

        Assert.Equal(before, scenario.State.OnboardingContracts);
        Assert.Equal(bound.Index, before.VoterCredentialRotations![leaf.IssueId].CredentialRevision);
        Assert.Equal(4, scenario.Wal.Count);

        void Revoke(Guid id)
        {
            var credential = image.Operations.Credentials[id] with { RevokedAtUtc = scenario.Time.Now };
            scenario.Store.Publish(image with { Operations = image.Operations with
                { Credentials = image.Operations.Credentials.SetItem(id, credential) } });
        }
    }

    [Theory]
    [InlineData("term")]
    [InlineData("recovery")]
    [InlineData("administrator")]
    [InlineData("majority")]
    [InlineData("competing-registration")]
    public async Task ApprovalCannotRegisterAfterAuthorityChanges(string change)
    {
        // Arrange
        using VoterContinuationSigningFixture fixture = new();
        var scenario = fixture.Scenario;
        var (context, _, oldLease) = await BindAsync(fixture);
        scenario.Time.Now = oldLease.LeaseUntilUtc;
        var request = await scenario.Authority.RequestAsync(
            await ProveAsync(fixture, context, VoterContinuationAuthority.REQUEST), scenario.Token);
        var approval = await scenario.Authority.AuthorizeAsync(context.Request.OperationId, request.Digest, "test-password", scenario.Token);
        var proof = await ProveAsync(fixture, context, VoterContinuationAuthority.REGISTER, approval.Digest);
        switch (change)
        {
            case "term":
                scenario.Term++;
                break;
            case "recovery":
                var image = scenario.State;
                scenario.Store.BeginRecovery();
                Assert.True(scenario.Store.ConfirmRecovery(image, scenario.Store.RecoveryVersion));
                break;
            case "administrator":
                scenario.ChangeActor(enabled: false);
                break;
            case "majority":
                scenario.Barrier.After = _ => throw new InvalidOperationException("Test majority lost");
                break;
            case "competing-registration":
                var competingRequest = context.Request with { OperationId = Guid.NewGuid() };
                var competing = context with
                {
                    Request = competingRequest, RequestDigest = VoterContinuationCommands.IntentDigest(competingRequest),
                    Process = context.Process with { BootId = Guid.NewGuid(), BootChallenge = RandomChallenge() }
                };
                var accepted = await scenario.Authority.RequestAsync(
                    await ProveAsync(fixture, competing, VoterContinuationAuthority.REQUEST), scenario.Token);
                var authorized = await scenario.Authority.AuthorizeAsync(competingRequest.OperationId, accepted.Digest,
                    "test-password", scenario.Token);
                var committed = await scenario.Authority.RegisterAsync(
                    await ProveAsync(fixture, competing, VoterContinuationAuthority.REGISTER, authorized.Digest),
                    authorized.Digest, scenario.Token);
                Assert.Equal(OperationOutcome.Committed, committed.Outcome);
                break;
        }

        int walCount = scenario.Wal.Count;
        var before = scenario.State.OnboardingContracts!;

        // Act
        Exception? refused = await Record.ExceptionAsync(() => scenario.Authority.RegisterAsync(proof, approval.Digest, scenario.Token));

        // Assert
        if (change == "majority")
        {
            Assert.IsType<InvalidOperationException>(refused);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(refused);
        }

        Assert.Equal(walCount, scenario.Wal.Count);
        Assert.Equal(before, scenario.State.OnboardingContracts);
        Assert.Null(before.VoterContinuations![context.Request.OperationId].Registration);
    }

    private static async Task<(VoterContinuationContext, CommandResult, OnlineProcessLease)> BindAsync(
        VoterContinuationSigningFixture fixture)
    {
        var scenario = fixture.Scenario;
        var (registration, artifact) = await VoterContinuationBindingTests.IssueAsync(fixture);
        var proof = await fixture.BindProofAsync(registration, artifact.CertificateDigest);
        var result = await fixture.Binding().BindAsync(proof, registration, artifact.CertificateDigest, scenario.Token);
        var bind = RevisionCodec.Decode(scenario.Wal.Last());
        var original = bind.VoterContinuation!.Context;
        var predecessor = scenario.State.OnboardingContracts!.OnlineProcesses![bind.EntityId];
        var request = original.Request with
        {
            OperationId = Guid.NewGuid(), BoundSource = new(original.Request.OperationId, result.Digest, result.Index)
        };
        var context = original with
        {
            Request = request, RequestDigest = VoterContinuationCommands.IntentDigest(request),
            Predecessor = predecessor,
            Process = new(Guid.NewGuid(), RandomChallenge(), predecessor.ProcessEpoch + 1, predecessor.RecoveryVersion)
        };
        return (context, result, predecessor);
    }

    private static async Task<VoterCredentialProofRequest> ProveAsync(VoterContinuationSigningFixture fixture,
        VoterContinuationContext context, string purpose,
        string? prior = null)
    {
        var scenario = fixture.Scenario;
        var request = context.Request;
        var candidate = scenario.Candidate with
        {
            BootId = context.Process.BootId, BootChallenge = context.Process.BootChallenge, ProcessEpoch = context.Process.Epoch,
            RecoveryVersion = context.Process.RecoveryVersion, IssuedAtUtc = scenario.Time.Now, Nonce = RandomChallenge()
        };
        var challenge = VoterCredentialContinuationProofs.SignChallenge(request, candidate, scenario.Crypto.Crypto.PeerKey,
            purpose, scenario.Options.PublicUri, prior);
        var call = new VoterCredentialProofRequest(request, challenge, Convert.ToBase64String(scenario.Crypto.Csr));
        string nonce = await scenario.Authority.IssueChallengeAsync(call, purpose, prior, scenario.Token);
        return fixture.Prove(nonce, request, candidate);
    }

    private static string RandomChallenge()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static VoterContinuationContext ReplaceSource(VoterContinuationContext context, VoterContinuationBoundSource source)
    {
        var request = context.Request with { BoundSource = source };
        return context with { Request = request, RequestDigest = VoterContinuationCommands.IntentDigest(request) };
    }
}
