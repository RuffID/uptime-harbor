using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialBindingDurabilityTests
{
    [Theory]
    [InlineData("bind-intent", false)]
    [InlineData("bind-intent", true)]
    [InlineData("bind-result", false)]
    [InlineData("bind-result", true)]
    public async Task InterruptedWrite_ReopensOriginalMaterialAndRecoversExactBind(string stage, bool after)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        byte[] key = File.ReadAllBytes(fixture.Local.PathFor("intent"));
        byte[] der = fixture.Local.Artifact!.CertificateDer.ToArray();
        fixture.Local.Files.FailStage = stage;
        fixture.Local.Files.AfterWrite = after;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.BindAsync());
        LocalVoterCredentialBindIntent? persisted = null;
        if (File.Exists(fixture.Local.PathFor("bind-intent")))
        {
            persisted = fixture.Intent;
        }

        fixture.Reopen();
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();
        LocalVoterCredentialBinding read = await fixture.ReadAsync();

        // Assert
        Assert.Equal(recovered, read);
        Assert.Equal(fixture.Result, recovered);
        Assert.Equal(3, recovered.CredentialRevision);
        if (persisted is not null)
        {
            Assert.Equal(persisted, fixture.Intent);
        }

        Assert.Equal(key, File.ReadAllBytes(fixture.Local.PathFor("intent")));
        Assert.Equal(der, (await fixture.Local.Store.ReadAsync(fixture.Local.Reference, fixture.Token)).Artifact!.CertificateDer);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.True(File.Exists(fixture.Local.PathFor("bind-result")));
        Assert.Null(fixture.Local.State.Current);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitOrLostResponse_ReplaysOnlyOriginalBindAfterReopen(bool committed)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        fixture.Transport.FailBeforeCommit = !committed;
        fixture.Transport.LoseResponse = committed;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.BindAsync());
        LocalVoterCredentialBindIntent intent = fixture.Intent;

        // Assert
        Assert.False(File.Exists(fixture.Local.PathFor("bind-result")));

        // Arrange
        fixture.Reopen();
        fixture.Transport.FailBeforeCommit = false;
        fixture.Transport.LoseResponse = false;
        fixture.Local.Authority.Scenario.State = fixture.Local.Authority.Scenario.State with { Term = 2 };
        fixture.Local.Authority.Publish();

        // Act
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();

        // Assert
        Assert.Equal(intent, fixture.Intent);
        Assert.Equal(intent.Command.OperationId, recovered.Result.OperationId);
        Assert.All(fixture.Transport.Sent, command => Assert.Equal(intent.Command, command));
        Assert.Equal(3, recovered.CredentialRevision);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        if (committed)
        {
            Assert.Single(fixture.Transport.Sent);
            Assert.Equal(1, recovered.Result.Term);
        }
        else
        {
            Assert.Equal(2, fixture.Transport.Sent.Count);
            Assert.Equal(2, recovered.Result.Term);
        }
    }

    [Fact]
    public async Task ConcurrentCoordinators_WaitForExistingTransitionAndCommitOneBind()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        LocalVoterCredentialBindingCoordinator other = fixture.CreateCoordinator();
        await fixture.Local.State.Transition.WaitAsync(fixture.Token);
        Task<LocalVoterCredentialBinding>[] pending;
        try
        {
            pending = Enumerable.Range(0, 6).Select(_ => other.BindAsync(fixture.Local.Reference,
                fixture.Local.Authority.SigningCall(), fixture.Token)).ToArray();
            Assert.All(pending, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            fixture.Local.State.Transition.Release();
        }

        // Act
        LocalVoterCredentialBinding[] results = await Task.WhenAll(pending);
        fixture.Reopen();
        LocalVoterCredentialBinding read = await fixture.ReadAsync();

        // Assert
        Assert.All(results, result => Assert.Equal(read, result));
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(2, fixture.Logs.Messages.Count);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
    }

    [Fact]
    public async Task BoundThenRetired_ReopensWithoutFreshOldKeyProofOrAnySignerCall()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        LocalVoterCredentialBinding bound = await fixture.BindAsync();
        fixture.Retire();
        fixture.Local.Authority.Time.Now = fixture.Local.Authority.Time.Now.AddDays(8);
        fixture.Local.Authority.Remote.Available = false;
        fixture.Reopen();

        // Act
        LocalVoterCredentialBinding replay = await fixture.BindAsync();
        LocalVoterCredentialBinding read = await fixture.ReadAsync();

        // Assert
        Assert.Equal(bound, replay);
        Assert.Equal(bound, read);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(2, fixture.Logs.Messages.Count);
    }

    [Fact]
    public async Task LostCommitAfterRetirement_RecoversMissingResultFromAcceptedEvidenceOnly()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        fixture.Transport.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.BindAsync());
        fixture.Retire();
        fixture.Local.Authority.Time.Now = fixture.Local.Authority.Time.Now.AddDays(8);
        fixture.Reopen();

        // Act
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();

        // Assert
        Assert.Equal(3, recovered.CredentialRevision);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(recovered, await fixture.ReadAsync());
    }

    [Fact]
    public async Task DurableIntentPrecedesExternalEffect_AndLogsDoNotClaimInstallation()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        fixture.Transport.BeforeCommit = command =>
        {
            Assert.Equal(command, fixture.Intent.Command);
            Assert.False(File.Exists(fixture.Local.PathFor("bind-result")));
            Assert.DoesNotContain(fixture.Logs.Messages, message => message.StartsWith("VoterCredentialLocalBindRecorded"));
        };

        // Act
        LocalVoterCredentialBinding bound = await fixture.BindAsync();
        await fixture.BindAsync();
        await fixture.ReadAsync();

        // Assert
        Assert.Equal(2, fixture.Logs.Messages.Count);
        Assert.Contains(fixture.Logs.Messages, message => message.Contains(bound.Result.OperationId.ToString()));
        string logs = string.Join('\n', fixture.Logs.Messages);
        Assert.DoesNotContain(fixture.Local.Local!.Intent.CsrDer, logs);
        Assert.DoesNotContain(Convert.ToBase64String(fixture.Local.Artifact!.CertificateDer), logs);
        Assert.DoesNotContain(fixture.Local.PreviousKeyReference, logs);
        Assert.DoesNotContain("Ready", logs);
        Assert.DoesNotContain("Installed", logs);
    }
}
