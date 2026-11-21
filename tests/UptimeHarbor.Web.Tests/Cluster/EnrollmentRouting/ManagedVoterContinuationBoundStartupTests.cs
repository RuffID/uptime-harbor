using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class ManagedVoterContinuationBoundStartupTests
{
    [Fact]
    public async Task NewBootReusesStillValidLeafAlreadyInLocalReplay()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = await ManagedVoterContinuationStartupFixture.OpenAsync(token);
        var (original, bind, _) = await BindBeforeChangingBootAsync(fixture, token);
        await fixture.StartAsync(token);
        var first = Assert.IsType<DynamicPeerTls.Snapshot>(fixture.Tls.Capture());
        await fixture.ReplayAcceptedBindAsync(token);
        fixture.ChangeBoot();
        fixture.Authority.SetTimeOffset(TimeSpan.FromMinutes(6));

        // Act
        await fixture.StartAsync(token);

        // Assert
        Assert.False(fixture.Tls.IsUsable(first));
        Assert.Null(fixture.Tls.Capture());
        Guid request = fixture.Diagnostics.Last();
        Assert.NotEqual(original, request);
        fixture.AssertClosed();

        // Act
        await fixture.ApproveAsync(request, token);
        await fixture.StartAsync(token);

        // Assert
        var next = Assert.IsType<DynamicPeerTls.Snapshot>(fixture.Tls.Capture());
        Assert.Equal(first.Certificate.RawData, next.Certificate.RawData);
        Assert.Equal(first.CredentialId, next.CredentialId);
        Assert.Equal(bind.Index, next.CredentialRevision);
        Assert.True(next.Quarantined);
        Assert.Equal(1, fixture.Authority.SigningJournal.Saves);
        Assert.Equal(7, fixture.Authority.Writes);
        fixture.AssertClosed();
    }

    [Theory]
    [InlineData("request")]
    [InlineData("register")]
    [InlineData("continuation-bind-result")]
    [InlineData("intent")]
    [InlineData("accepted-request")]
    [InlineData("authorize")]
    [InlineData("accepted-register")]
    [InlineData("install-manifest")]
    [InlineData("install-reference")]
    [InlineData("install-confirmation")]
    public async Task NewBootReplaysInterruptedBoundRecoveryAndPublishesOriginalLeaf(string interruption)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = await ManagedVoterContinuationStartupFixture.OpenAsync(token);
        var (original, bind, oldLease) = await BindBeforeChangingBootAsync(fixture, token);
        fixture.ChangeBoot();
        Guid boot = fixture.Installation.Current!.BootId;
        TimeSpan deadline = oldLease.LeaseUntilUtc - fixture.Authority.GetUtcNow();
        fixture.Authority.SetTimeOffset(deadline - TimeSpan.FromTicks(1));

        // Act
        await fixture.StartAsync(token);

        // Assert
        Assert.Equal(4, fixture.Authority.Writes);
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();

        fixture.Authority.SetTimeOffset(deadline);
        string directory = Path.GetDirectoryName(fixture.IntentPath)!;
        string protectedStage = interruption switch
        {
            "intent" => $"continuation-recovery-{boot:N}-intent",
            "accepted-request" => $"continuation-recovery-{boot:N}-request",
            "authorize" => $"continuation-recovery-{boot:N}-authorize",
            "accepted-register" => $"continuation-recovery-{boot:N}-register",
            _ => interruption
        };
        bool loseResponse = interruption is "request" or "register";
        if (loseResponse)
        {
            fixture.Handler.LoseResponse = interruption;
        }

        bool interrupted = false;
        fixture.Handler.BeforeSend = path =>
        {
            Assert.Equal(0, fixture.Installation.Transition.CurrentCount);
            Assert.DoesNotContain(path.Split('/')[^1], new[] { "issue", "bind" });
            if (!loseResponse && !interrupted && File.Exists(Path.Combine(directory, protectedStage)))
            {
                interrupted = true;
                throw new HttpRequestException("Read interrupted after a protected write.");
            }
        };

        // Act
        await fixture.StartAsync(token);
        await fixture.StartAsync(token);

        // Assert
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();
        Assert.Equal(5, fixture.Authority.Writes);
        Guid request = fixture.Diagnostics.Last();
        Assert.NotEqual(original, request);
        var accepted = fixture.Authority.ReadImage().OnboardingContracts!.VoterContinuations!;
        var context = accepted[request].Request.VoterContinuation!.Context;
        Assert.Equal(original, context.BoundSource!.RequestId);
        Assert.Equal(bind, fixture.Authority.ReadImage().Results[accepted[original].Bind!.OperationId]);
        Assert.Equal(bind.Digest, context.BoundSource.BindDigest);
        Assert.Equal(bind.Index, context.BoundSource.CredentialRevision);
        Assert.Equal(oldLease, context.Predecessor);
        Assert.Equal(boot, context.Process.BootId);
        Assert.Null(accepted[request].Authorization);

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ApproveAsync(request, token,
            fixture.Authority.ReadImage().Results[original].Digest));
        await fixture.StartAsync(token);

        // Assert
        Assert.Equal(5, fixture.Authority.Writes);
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();

        // Act
        await fixture.ApproveAsync(request, token);
        await fixture.StartAsync(token);
        if (interruption is "register" or "authorize" or "accepted-register" or "install-manifest" or "install-reference"
            or "install-confirmation")
        {
            Assert.Null(fixture.Tls.Capture());
            fixture.AssertClosed();
            await fixture.StartAsync(token);
        }

        // Assert
        Assert.True(loseResponse || interrupted);
        DynamicPeerTls.Snapshot snapshot = Assert.IsType<DynamicPeerTls.Snapshot>(fixture.Tls.Capture());
        var image = fixture.Authority.ReadImage();
        var originalBind = image.OnboardingContracts!.VoterContinuations![original].Bind!;
        var recovery = image.OnboardingContracts.VoterContinuations[request];
        var activeLease = image.OnboardingContracts.OnlineProcesses![fixture.Authority.VoterId];
        Assert.Equal(bind, image.Results[originalBind.OperationId]);
        Assert.NotNull(recovery.Authorization);
        Assert.NotNull(recovery.Registration);
        Assert.Null(recovery.Bind);
        Assert.Equal(boot, activeLease.BootId);
        Assert.Equal(oldLease.ProcessEpoch + 1, activeLease.ProcessEpoch);
        Assert.Equal(recovery.Registration.OperationId, activeLease.GrantId);
        Assert.True(activeLease.CredentialContinuation);
        Assert.Equal(fixture.Reference, snapshot.ContinuationReference);
        Assert.Equal(originalBind.VoterContinuation!.Credential!.Id, snapshot.CredentialId);
        Assert.Equal(bind.Index, snapshot.CredentialRevision);
        Assert.Equal(originalBind.VoterContinuation.Credential.PublicCertificate, Convert.ToBase64String(snapshot.Certificate.RawData));
        Assert.True(snapshot.Quarantined);
        Assert.False(fixture.Tls.Validate(snapshot, snapshot.Certificate));
        Assert.Equal(7, fixture.Authority.Writes);
        Assert.Equal(1, fixture.Authority.SigningJournal.Saves);
        fixture.AssertClosed();
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("previous-revoked")]
    [InlineData("bound-revoked")]
    [InlineData("process-conflict")]
    [InlineData("history-conflict")]
    [InlineData("majority")]
    public async Task NewBootKeepsAllGatesClosedWhenBoundRecoveryEvidenceChanges(string failure)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = await ManagedVoterContinuationStartupFixture.OpenAsync(token);
        var (original, _, oldLease) = await BindBeforeChangingBootAsync(fixture, token);
        RevisionImage historical = fixture.Authority.ReadImage();
        fixture.ChangeBoot();
        fixture.Authority.SetTimeOffset(oldLease.LeaseUntilUtc - fixture.Authority.GetUtcNow());
        await fixture.StartAsync(token);
        Guid request = fixture.Diagnostics.Last();
        Assert.NotEqual(original, request);
        await fixture.ApproveAsync(request, token);
        switch (failure)
        {
            case "rollback":
                fixture.Authority.ChangeImage(_ => historical);
                break;
            case "previous-revoked":
            case "bound-revoked":
                Guid revoked = failure == "previous-revoked" ? fixture.PreviousCredential
                    : historical.OnboardingContracts!.VoterContinuations![original].Bind!.VoterContinuation!.Credential!.Id;
                fixture.Authority.ChangeImage(image => image with { Operations = image.Operations with
                {
                    Credentials = image.Operations.Credentials.SetItem(revoked,
                        image.Operations.Credentials[revoked] with { RevokedAtUtc = fixture.Authority.GetUtcNow() })
                } });
                break;
            case "process-conflict":
                fixture.Handler.TransformSlice = slice => slice with { Process = slice.Process! with { BootId = Guid.NewGuid() } };
                break;
            case "history-conflict":
                fixture.Handler.TransformSlice = slice => slice with { History = slice.History! with
                {
                    Bound = slice.History.Bound with { Decision = slice.History.Bound.Decision with { Digest = new string('F', 64) } }
                } };
                break;
            case "majority":
                fixture.Authority.Majority = false;
                break;
        }

        // Act
        await fixture.StartAsync(token);

        // Assert
        Assert.Equal(6, fixture.Authority.Writes);
        Assert.Equal(1, fixture.Authority.SigningJournal.Saves);
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();
    }

    private static async Task<(Guid Original, CommandResult Bind, OnlineProcessLease Lease)> BindBeforeChangingBootAsync(
        ManagedVoterContinuationStartupFixture fixture, CancellationToken token)
    {
        await fixture.StartAsync(token);
        Guid original = Assert.Single(fixture.Diagnostics);
        await fixture.ApproveAsync(original, token);
        fixture.Handler.LoseResponse = "bind";
        await fixture.StartAsync(token);
        RevisionImage image = fixture.Authority.ReadImage();
        var accepted = image.OnboardingContracts!.VoterContinuations![original];
        RevisionCommand bind = Assert.IsType<RevisionCommand>(accepted.Bind);
        Assert.Equal(4, fixture.Authority.Writes);
        Assert.Null(fixture.Tls.Capture());
        fixture.AssertClosed();
        return (original, image.Results[bind.OperationId], image.OnboardingContracts.OnlineProcesses![fixture.Authority.VoterId]);
    }
}
