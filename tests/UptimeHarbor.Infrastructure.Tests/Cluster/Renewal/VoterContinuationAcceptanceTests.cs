using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterContinuationAcceptanceTests
{
    [Fact]
    public async Task AcceptedTransitionsAndExactReplayUseOneWalAndPreserveMembershipAndCredentials()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        RevisionImage initial = scenario.State;

        // Act
        CommandResult proposed = await scenario.ProposeAsync();
        CommandResult authorized = await scenario.ApproveAsync(proposed);
        CommandResult registered = await scenario.RegisterAsync(authorized);
        CommandResult replay = await scenario.RegisterAsync(authorized);
        CommandResult adminReplay = await scenario.ApproveAsync(proposed);
        CommandResult requestReplay = await scenario.ProposeAsync();

        // Assert
        Assert.All(new[] { proposed, authorized, registered }, item => Assert.Equal(OperationOutcome.Committed, item.Outcome));
        Assert.Equal(authorized, adminReplay);
        Assert.Equal(proposed, requestReplay);
        Assert.Equal(registered, replay);
        Assert.Equal(3, scenario.Wal.Count);
        Assert.Same(initial.Operations, scenario.State.Operations);
        OnlineProcessLease process = scenario.State.OnboardingContracts!.OnlineProcesses![scenario.Candidate.OriginNodeId];
        Assert.True(process.CredentialContinuation);
        Assert.Equal(scenario.Time.Now.AddMinutes(2), process.LeaseUntilUtc);
        foreach (byte[] payload in scenario.Wal)
        {
            RevisionCommand command = RevisionCodec.Decode(payload);
            Assert.Equal(payload, RevisionCodec.Encode(command));
            Assert.NotEqual(RevisionCodec.Digest(payload), RevisionCodec.Digest(command));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                scenario.Dispatcher.AcceptAsync(command, command.OriginNodeId, scenario.Token));
        }

        Assert.Equal(3, scenario.Wal.Count);
    }

    [Fact]
    public async Task LostApplyResponseAndLaterFreshProofReturnOriginalRegistration()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult authorized = await scenario.ApproveAsync(await scenario.ProposeAsync());
        VoterCredentialProofRequest call = await scenario.ProveAsync(authorized.Digest);
        scenario.LoseResponse = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.Authority.RegisterAsync(call, authorized.Digest, scenario.Token));
        scenario.LoseResponse = false;
        scenario.Time.Now = scenario.Time.Now.AddSeconds(61);
        Task<CommandResult> reused = scenario.Authority.RegisterAsync(call, authorized.Digest, scenario.Token);
        CommandResult recovered = await scenario.RegisterAsync(authorized);

        // Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reused);
        Assert.Equal(scenario.State.Index, recovered.Index);
        Assert.Equal(3, scenario.Wal.Count);
    }

    [Theory]
    [InlineData("local-setup")]
    [InlineData("incorrect-password")]
    [InlineData("actor-changed")]
    [InlineData("actor-revision-changed")]
    [InlineData("term-changed")]
    [InlineData("recovery-changed")]
    [InlineData("no-majority")]
    [InlineData("no-leader")]
    public async Task ApprovalRequiresExactCurrentAuthority(string failure)
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult proposed = await scenario.ProposeAsync();
        switch (failure)
        {
            case "local-setup":
                scenario.CommonActorId = null;
                break;
            case "incorrect-password":
                scenario.AcceptPassword = false;
                break;
            case "actor-changed":
                scenario.CompletePasswordCheck = () => scenario.CommonActorId = Guid.NewGuid();
                break;
            case "actor-revision-changed":
                scenario.CompletePasswordCheck = () => scenario.ChangeActor(enabled: true);
                break;
            case "term-changed":
                scenario.CompletePasswordCheck = () => scenario.Term++;
                break;
            case "recovery-changed":
                scenario.CompletePasswordCheck = () =>
                {
                    scenario.Store.BeginRecovery();
                    Assert.True(scenario.Store.ConfirmRecovery(scenario.State, scenario.Store.RecoveryVersion));
                };
                break;
            case "no-majority":
                scenario.Barrier.Available = false;
                break;
            case "no-leader":
                scenario.NativeLeader = false;
                break;
        }

        // Act
        Exception? error = await Record.ExceptionAsync(() => scenario.ApproveAsync(proposed));

        // Assert
        Assert.NotNull(error);
        if (failure == "no-majority")
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
        }

        Assert.Single(scenario.Wal);
    }

    [Fact]
    public async Task ReducerRejectsAdministratorChangedBetweenBarrierAndApply()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult authorized = await scenario.ApproveAsync(await scenario.ProposeAsync());
        scenario.BeforeApply = () => scenario.ChangeActor(enabled: false);

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.RegisterAsync(authorized));

        // Assert
        CommandResult decision = scenario.State.Results.Values.Single(item => item.Index == scenario.State.Index);
        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Equal("VoterContinuationAdministratorChanged", decision.Reason);
        Assert.Null(scenario.State.OnboardingContracts!.OnlineProcesses);
    }

    [Fact]
    public async Task ConflictingWalRetryCannotReplaceFirstDecisionOrBreakReplay()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult proposed = await scenario.ProposeAsync();
        RevisionCommand first = RevisionCodec.Decode(scenario.Wal.Single());
        VoterContinuationCommand data = first.VoterContinuation!;
        RevisionCommand different = first with
        {
            VoterContinuation = data with { Evidence = data.Evidence! with { ProofDigest = new string('A', 64) } }
        };

        // Act
        RevisionImage replay = new RevisionReducer().Apply(scenario.State, different, scenario.State.Index + 1, scenario.Term);

        // Assert
        Assert.NotEqual(proposed.Digest, RevisionCodec.Digest(different));
        Assert.Equal(proposed, replay.Results[proposed.OperationId]);
        Assert.Same(scenario.State.OnboardingContracts, replay.OnboardingContracts);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.ApproveAsync(proposed with { Digest = RevisionCodec.Digest(different) }));
        Assert.Single(scenario.Wal);
    }

    [Fact]
    public async Task SameBootRegistrationKeepsDeadlineAndReplayClosesAtExpiry()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        OnlineProcessLease predecessor = new(scenario.Candidate.OriginNodeId, Guid.NewGuid(), scenario.Candidate.BootId,
            scenario.Candidate.BootChallenge, 1, 0, scenario.Time.Now.AddSeconds(30), scenario.State.Index);
        scenario.Store.Publish(scenario.State with
        {
            OnboardingContracts = scenario.State.OnboardingContracts! with
            {
                OnlineProcesses = ImmutableDictionary<Guid, OnlineProcessLease>.Empty.Add(predecessor.NodeId, predecessor)
            }
        });
        CommandResult authorized = await scenario.ApproveAsync(await scenario.ProposeAsync());

        // Act
        await scenario.RegisterAsync(authorized);
        scenario.Time.Now = predecessor.LeaseUntilUtc;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.RegisterAsync(authorized));

        // Assert
        OnlineProcessLease registration = scenario.State.OnboardingContracts!.OnlineProcesses![predecessor.NodeId];
        Assert.Equal(predecessor.LeaseUntilUtc, registration.LeaseUntilUtc);
        Assert.Equal(3, scenario.Wal.Count);
    }

    [Fact]
    public async Task RegistrationRejectsExpiredAdminConfirmationAtExactBoundary()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult authorized = await scenario.ApproveAsync(await scenario.ProposeAsync());

        // Act
        scenario.Time.Now = scenario.Time.Now.AddMinutes(5);
        UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.RegisterAsync(authorized));

        // Assert
        Assert.Equal("VoterContinuationAuthorizationExpiredOrChanged", error.Message);
        Assert.Equal(2, scenario.Wal.Count);
        Assert.Null(scenario.State.OnboardingContracts!.OnlineProcesses);
    }

    [Fact]
    public async Task RestartUsesExactLeaseBoundaryAndRejectsHistoricalBootAtHigherEpoch()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        VoterCredentialProof firstBoot = scenario.Candidate;
        CommandResult firstApproval = await scenario.ApproveAsync(await scenario.ProposeAsync());
        await scenario.RegisterAsync(firstApproval);
        OnlineProcessLease original = scenario.State.OnboardingContracts!.OnlineProcesses![firstBoot.OriginNodeId];
        VoterCredentialProofRequest oldProof = await scenario.ProveAsync(firstApproval.Digest);
        scenario.NextProcess();

        // Act
        scenario.Time.Now = original.LeaseUntilUtc.AddTicks(-1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.ProposeAsync());
        scenario.Time.Now = original.LeaseUntilUtc;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.RegisterAsync(oldProof, firstApproval.Digest, scenario.Token));
        CommandResult registered = await scenario.RegisterAsync(await scenario.ApproveAsync(await scenario.ProposeAsync()));

        // Assert
        OnlineProcessLease current = scenario.State.OnboardingContracts.OnlineProcesses[firstBoot.OriginNodeId];
        Assert.Equal(registered.Index, current.Index);
        Assert.Equal(original.ProcessEpoch + 1, current.ProcessEpoch);
        Assert.NotEqual(original.BootId, current.BootId);
        OnboardingSnapshot.Validate(scenario.State);

        // Act
        scenario.Time.Now = current.LeaseUntilUtc;
        scenario.NextProcess(firstBoot);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.ProposeAsync());

        // Assert
        Assert.Equal(6, scenario.Wal.Count);
    }

    [Fact]
    public async Task RegistrationRequiresASeparateNonceForExactAuthorizationAndSameBoot()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult proposed = await scenario.ProposeAsync();
        CommandResult authorized = await scenario.ApproveAsync(proposed);
        VoterCredentialProofRequest wrongPhase = await scenario.ProveAsync();
        VoterCredentialProofRequest exact = await scenario.ProveAsync(authorized.Digest);

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.RegisterAsync(wrongPhase, authorized.Digest, scenario.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.RegisterAsync(exact, proposed.Digest, scenario.Token));
        CommandResult result = await scenario.Authority.RegisterAsync(exact, authorized.Digest, scenario.Token);
        scenario.ChangeActor(enabled: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.RegisterAsync(authorized));

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(3, scenario.Wal.Count);
    }

    [Fact]
    public async Task SnapshotAndRecoveryValidateRegistrationAndWalRecreatesTheSameDecision()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        RevisionImage initial = scenario.State;
        CommandResult registered = await scenario.RegisterAsync(await scenario.ApproveAsync(await scenario.ProposeAsync()));
        Guid requestId = scenario.Request.OperationId;
        OnboardingContractImage contracts = scenario.State.OnboardingContracts!;
        string root = Path.Combine(Path.GetTempPath(), "uh-continuation-" + Guid.NewGuid().ToString("N"));
        scenario.Options.StoragePath = root;
        RevisionSnapshotStore snapshots = new(Options.Create(scenario.Options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(scenario.State, scenario.Token);

        try
        {
            // Act
            PublishedRevisionSnapshot loaded = await snapshots.LoadAsync(snapshot.Manifest.SnapshotId, snapshot.Digest, scenario.Token);
            RevisionStateStore replay = new();
            replay.Publish(initial);
            replay.BeginRecovery();
            RevisionRecoveryTests.Projection projection = new(initial);
            RevisionApplyEngine engine = new(replay, new RevisionReducer(), projection);
            foreach (byte[] payload in scenario.Wal)
            {
                RevisionCommand command = RevisionCodec.Decode(payload);
                await engine.ApplyAsync(command, replay.Current!.Index + 1, command.VoterContinuation!.Acceptance!.Term,
                    scenario.Options.NodeId, scenario.Token);
            }

            Assert.False(replay.Available);
            bool recovered = await engine.ConfirmRecoveryAsync(async image =>
            {
                await projection.VerifyAsync(image, scenario.Token);
                return true;
            }, scenario.Token);
            scenario.Crypto.State = loaded.Image;
            RevisionImage archived = scenario.Crypto.Archived();
            OnboardingSnapshot.Validate(archived);

            // Assert
            Assert.True(recovered);
            Assert.True(replay.Available);
            Assert.Equal(3, projection.Effects);
            Assert.Equal(registered, replay.Current!.Results[registered.OperationId]);
            Assert.Equal(registered, loaded.Image.Results[registered.OperationId]);
            Assert.Equal(contracts.VoterContinuations![requestId], loaded.Image.OnboardingContracts!.VoterContinuations![requestId]);
            Assert.Null(archived.OnboardingContracts);
            Assert.Equal(contracts.VoterContinuations[requestId], archived.RecoveryBaseline!.PreviousOnboardingContracts!.VoterContinuations![requestId]);
            Assert.Throws<InvalidDataException>(() => OnboardingSnapshot.Validate(loaded.Image with
            {
                OnboardingContracts = loaded.Image.OnboardingContracts with { OnlineProcesses = ImmutableDictionary<Guid, OnlineProcessLease>.Empty }
            }));
            Assert.Throws<InvalidDataException>(() => OnboardingSnapshot.Validate(archived with
            {
                Results = archived.Results.SetItem(registered.OperationId, registered with { Digest = new string('B', 64) })
            }));
        }
        finally
        {
            File.Delete(snapshot.Path);
            Directory.Delete(Path.Combine(root, "snapshots"));
            Directory.Delete(root);
        }
    }
}
