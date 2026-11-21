using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialBindingBoundaryTests
{
    [Theory]
    [InlineData("previous-key")]
    [InlineData("wrapping-key")]
    [InlineData("new-key")]
    [InlineData("previous-der")]
    public async Task BoundRecoveryRequiresOriginalKeysAndHistoricalDer(string material)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        if (material == "previous-key")
        {
            string key = fixture.Local.PreviousKeyReference.Split(':')[0] + ".key";
            File.Delete(Path.Combine(fixture.Local.Authority.Installation.Options.KeyDirectory, key));
        }
        else if (material == "wrapping-key")
        {
            File.Delete(Path.Combine(fixture.Local.Authority.Installation.Options.SecretDirectory, "wrapping.key"));
        }
        else if (material == "new-key")
        {
            fixture.Local.RewriteIntent(intent => intent, true);
        }
        else
        {
            RevisionImage image = fixture.Local.Authority.Scenario.State;
            Guid previousId = fixture.Local.Authority.Request.Renewal.PreviousCredentialId;
            OperationalCredential old = image.Operations.Credentials[previousId];
            fixture.Local.Authority.Scenario.State = image with { Operations = image.Operations with
                { Credentials = image.Operations.Credentials.SetItem(previousId, old with { PublicCertificate = Convert.ToBase64String([1, 2, 3]) }) } };
            fixture.Local.Authority.Publish();
        }

        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
        Assert.Equal(2, fixture.Logs.Messages.Count);
    }

    [Fact]
    public async Task CancellationAfterCommit_ReopensOriginalBindWithoutRedispatch()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        fixture.Transport.AfterCommit = cancellation.Cancel;

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Coordinator.BindAsync(fixture.Local.Reference,
            fixture.Local.Authority.SigningCall(), cancellation.Token));
        LocalVoterCredentialBindIntent intent = fixture.Intent;
        fixture.Reopen();
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(intent, fixture.Intent);
        Assert.Equal(intent.Command.OperationId, recovered.Result.OperationId);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("intent")]
    [InlineData("commit")]
    [InlineData("result")]
    [InlineData("replay")]
    public async Task MajorityLossAtEachBoundary_DoesNotPublishFalseSuccess(string boundary)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        if (boundary is "start" or "replay")
        {
            if (boundary == "replay")
            {
                await fixture.BindAsync();
            }

            fixture.Local.Authority.SubjectBarrier.Available = false;
        }
        else if (boundary == "commit")
        {
            fixture.Transport.AfterCommit = () => fixture.Local.Authority.SubjectBarrier.Available = false;
        }
        else
        {
            fixture.Local.Files.BeforeWrite = stage =>
            {
                if (stage == "bind-" + boundary)
                {
                    fixture.Local.Authority.SubjectBarrier.Available = false;
                }
            };
        }

        int logs = fixture.Logs.Messages.Count(message => message.StartsWith("VoterCredentialLocalBindRecorded"));

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Equal(logs, fixture.Logs.Messages.Count(message => message.StartsWith("VoterCredentialLocalBindRecorded")));
        Assert.False(fixture.Local.State.CommonOpen);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        if (boundary is "start" or "intent")
        {
            Assert.Empty(fixture.Transport.Sent);
        }

        if (boundary != "start")
        {
            // Arrange
            fixture.Local.Authority.SubjectBarrier.Available = true;
            fixture.Reopen();

            // Act
            LocalVoterCredentialBinding recovered = await fixture.BindAsync();

            // Assert
            Assert.Equal(recovered, await fixture.ReadAsync());
            Assert.Single(fixture.Transport.Sent);
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("digest")]
    [InlineData("index")]
    [InlineData("applied")]
    [InlineData("term")]
    [InlineData("outcome")]
    [InlineData("reason")]
    public async Task AlteredTransportResponse_RequiresAcceptedReconciliationBeforePin(string field)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        fixture.Transport.AlterResponse = result => LocalVoterCredentialBindingValidationTests.Change(result, field);

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.Local.PathFor("bind-result")));
        Assert.Single(fixture.Logs.Messages);

        // Arrange
        fixture.Reopen();

        // Act
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();

        // Assert
        Assert.Equal(recovered, await fixture.ReadAsync());
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
    }

    [Fact]
    public async Task TransportSuccessWithoutAcceptedBind_DoesNotCreateResultOrCredentialRevision()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        fixture.Transport.DoNotCommit = true;

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.Local.PathFor("bind-result")));
        Assert.Single(fixture.Logs.Messages);
        Assert.Equal(2, fixture.Local.Authority.Scenario.State.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedLocalRollbackAfterBound_CannotGenerateAnotherBindOrLeaf(bool removeEverything)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        File.Delete(fixture.Local.PathFor("bind-result"));
        File.Delete(fixture.Local.PathFor("bind-intent"));
        if (removeEverything)
        {
            File.Delete(fixture.Local.PathFor("artifact"));
            File.Delete(fixture.Local.PathFor("authorization"));
            File.Delete(fixture.Local.PathFor("intent"));
        }

        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.Local.PathFor("bind-result")));
        Assert.False(File.Exists(fixture.Local.PathFor("bind-intent")));
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
    }

    [Fact]
    public async Task AcceptedRollbackBelowSavedRevision_RefusesWithoutResending()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        RevisionImage authorized = fixture.Local.Authority.Scenario.State;
        await fixture.BindAsync();
        fixture.Local.Authority.Scenario.State = authorized;
        fixture.Local.Authority.Publish();
        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());
        Exception? readError = await Record.ExceptionAsync(() => fixture.ReadAsync());

        // Assert
        Assert.NotNull(error);
        Assert.NotNull(readError);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(2, fixture.Logs.Messages.Count);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
    }

    [Fact]
    public async Task MissingResult_IsIncompleteUntilExplicitFreshRecovery()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        LocalVoterCredentialBinding expected = await fixture.BindAsync();
        File.Delete(fixture.Local.PathFor("bind-result"));
        fixture.Reopen();

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadAsync());
        LocalVoterCredentialBinding recovered = await fixture.BindAsync();

        // Assert
        Assert.Equal(expected, recovered);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("credential-revoked")]
    [InlineData("expired")]
    [InlineData("issuer-revoked")]
    [InlineData("foreign-local-node")]
    public async Task SavedResultDoesNotReplaceFreshLiveAuthority(string failure)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        RevisionImage image = fixture.Local.Authority.Scenario.State;
        Guid issue = fixture.Local.Authority.Request.Renewal.Issuance.IssueId;
        if (failure == "recovery")
        {
            fixture.Local.Authority.SubjectBarrier.After = _ => fixture.Local.Authority.Subject.Invalidate();
        }
        else if (failure == "expired")
        {
            fixture.Local.Authority.Time.Now = fixture.Local.Authority.Request.Renewal.Issuance.NotAfterUtc;
        }
        else if (failure == "foreign-local-node")
        {
            fixture.Local.Authority.SubjectOptions.NodeId = fixture.Local.Authority.IssuerOptions.NodeId;
            fixture.Local.Authority.SubjectOptions.StorageGeneration = fixture.Local.Authority.IssuerOptions.StorageGeneration;
        }
        else if (failure == "credential-revoked")
        {
            OperationalCredential credential = image.Operations.Credentials[issue];
            image = image with { Operations = image.Operations with
                { Credentials = image.Operations.Credentials.SetItem(issue, credential with { RevokedAtUtc = fixture.Local.Authority.Time.Now }) } };
        }
        else
        {
            Guid epoch = fixture.Local.Authority.Request.Renewal.Issuance.IssuerEpoch;
            AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![epoch];
            image = image with { OnboardingContracts = image.OnboardingContracts with
                { Issuers = image.OnboardingContracts.Issuers.SetItem(epoch, issuer with { RevokedAtUtc = fixture.Local.Authority.Time.Now }) } };
        }

        fixture.Local.Authority.Scenario.State = image;
        fixture.Local.Authority.Publish();
        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.ReadAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(2, fixture.Logs.Messages.Count);
    }
}
