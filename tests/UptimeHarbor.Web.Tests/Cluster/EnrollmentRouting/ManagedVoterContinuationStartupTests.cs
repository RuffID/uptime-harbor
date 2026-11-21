using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class ManagedVoterContinuationStartupTests
{
    [Theory]
    [InlineData("request")]
    [InlineData("register")]
    [InlineData("issue")]
    [InlineData("bind")]
    [InlineData("install-confirmation")]
    public async Task StartupResumesExactIntentAndPublishesOnlyInQuarantine(string lostResponse)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedVoterContinuationStartupFixture fixture = await ManagedVoterContinuationStartupFixture.OpenAsync(token);
        bool loseConfirmationRead = lostResponse == "install-confirmation";
        if (!loseConfirmationRead)
        {
            fixture.Handler.LoseResponse = lostResponse;
        }

        string confirmation = Path.Combine(Path.GetDirectoryName(fixture.IntentPath)!, "install-confirmation");
        string prefix = "/api/credential-continuation/v1/";
        List<string> sent = [];
        fixture.Handler.BeforeSend = path =>
        {
            Assert.Equal(0, fixture.Installation.Transition.CurrentCount);
            Assert.True(File.Exists(fixture.IntentPath));
            sent.Add(path);
            if (loseConfirmationRead && File.Exists(confirmation))
            {
                loseConfirmationRead = false;
                throw new HttpRequestException("Fresh proof lost after the local confirmation write.");
            }
        };

        // Act
        await fixture.StartAsync(token);

        // Assert
        fixture.AssertClosed();
        Assert.Contains(prefix + "request", sent);
        Assert.Equal(1, fixture.Authority.Writes);
        AcceptedVoterContinuation initial = Assert.Single(fixture.Authority.ReadImage().OnboardingContracts!.VoterContinuations!).Value;
        Guid requestId = initial.Request.OperationId;
        string protectedIntent = await File.ReadAllTextAsync(fixture.IntentPath, token);
        Assert.DoesNotContain(requestId.ToString(), protectedIntent);
        if (lostResponse == "request")
        {
            Assert.Empty(fixture.Diagnostics);
        }

        // Act
        await fixture.StartAsync(token);

        // Assert
        fixture.AssertClosed();
        Assert.Equal(requestId, Assert.Single(fixture.Diagnostics));
        Assert.Equal(1, fixture.Authority.Writes);

        // Act
        await fixture.ApproveAsync(requestId, token);
        await fixture.StartAsync(token);

        // Assert
        fixture.AssertClosed();
        if (lostResponse != "request")
        {
            Assert.Null(fixture.Tls.Capture());
            await fixture.StartAsync(token);
        }

        DynamicPeerTls.Snapshot snapshot = Assert.IsType<DynamicPeerTls.Snapshot>(fixture.Tls.Capture());
        var accepted = Assert.Single(fixture.Authority.ReadImage().OnboardingContracts!.VoterContinuations!).Value;
        RevisionCommand bind = Assert.IsType<RevisionCommand>(accepted.Bind);
        CommandResult decision = fixture.Authority.ReadImage().Results[bind.OperationId];
        Assert.True(snapshot.Quarantined);
        Assert.Equal(fixture.Reference, snapshot.ContinuationReference);
        Assert.Equal(initial.Request, accepted.Request);
        Assert.Equal(initial.Request.VoterContinuation!.Context.Request.Renewal.Issuance.IssueId, snapshot.CredentialId);
        Assert.Equal(decision.Index, snapshot.CredentialRevision);
        Assert.Equal(bind.VoterContinuation!.Credential!.PublicCertificate, Convert.ToBase64String(snapshot.Certificate.RawData));
        Assert.False(fixture.Tls.Validate(snapshot, snapshot.Certificate));
        Assert.Equal(protectedIntent, await File.ReadAllTextAsync(fixture.IntentPath, token));
        Assert.True(File.Exists(confirmation));
        Assert.Equal(4, fixture.Authority.Writes);
        Assert.Equal(1, fixture.Authority.SigningJournal.Saves);
        Assert.Equal(requestId, Assert.Single(fixture.Diagnostics));
        fixture.AssertClosed();

        fixture.Authority.Majority = false;

        // Act
        await fixture.StartAsync(token);

        // Assert
        Assert.Null(fixture.Tls.Capture());
        Assert.False(fixture.Tls.IsUsable(snapshot));
        fixture.AssertClosed();
    }

    [Theory]
    [InlineData("history")]
    [InlineData("request-history")]
    [InlineData("revoked")]
    [InlineData("boot")]
    public async Task StartupStopsBeforeNextOperationWhenEvidenceChanges(string failure)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedVoterContinuationStartupFixture fixture = await ManagedVoterContinuationStartupFixture.OpenAsync(token);
        await fixture.StartAsync(token);
        Guid request = Assert.Single(fixture.Diagnostics);
        await fixture.ApproveAsync(request, token);
        switch (failure)
        {
            case "history":
                fixture.Authority.ChangeImage(image => image with { Results = image.Results.Remove(fixture.ReplayOperation) });
                break;
            case "request-history":
                fixture.Authority.ChangeImage(image => image with
                {
                    Results = image.Results.Remove(request)
                        .Remove(image.OnboardingContracts!.VoterContinuations![request].Authorization!.OperationId),
                    OnboardingContracts = image.OnboardingContracts! with
                    {
                        VoterContinuations = image.OnboardingContracts.VoterContinuations!.Remove(request)
                    }
                });
                break;
            case "revoked":
                fixture.Authority.ChangeImage(image =>
                {
                    var previous = image.Operations.Credentials[fixture.PreviousCredential];
                    return image with { Operations = image.Operations with { Credentials = image.Operations.Credentials
                        .SetItem(previous.Id, previous with { RevokedAtUtc = fixture.Authority.GetUtcNow() }) } };
                });
                break;
            case "boot":
                fixture.ChangeBoot();
                break;
        }

        List<string> sent = [];
        fixture.Handler.BeforeSend = sent.Add;

        // Act
        await fixture.StartAsync(token);

        // Assert
        if (failure == "boot")
        {
            Assert.Empty(sent);
        }
        else
        {
            Assert.Equal("/api/credential-continuation/v1/accepted-slice", Assert.Single(sent));
        }

        Assert.Equal(2, fixture.Authority.Writes);
        Assert.Equal(0, fixture.Authority.SigningJournal.Saves);
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();
    }
}
