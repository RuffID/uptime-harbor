using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialInstallationTests
{
    private const string MANIFEST = "install-manifest";
    private const string REFERENCE = "install-reference";
    private const string CONFIRMATION = "install-confirmation";

    [Theory]
    [InlineData(MANIFEST, false)]
    [InlineData(MANIFEST, true)]
    [InlineData(REFERENCE, false)]
    [InlineData(REFERENCE, true)]
    [InlineData(CONFIRMATION, false)]
    [InlineData(CONFIRMATION, true)]
    public async Task InterruptedWrite_ReopensAndCompletesOriginalInstallation(string stage, bool after)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        byte[] keyBundle = File.ReadAllBytes(fixture.Local.PathFor("intent"));
        byte[] artifact = File.ReadAllBytes(fixture.Local.PathFor("artifact"));
        fixture.Local.Files.FailStage = stage;
        fixture.Local.Files.AfterWrite = after;

        // Act
        await Assert.ThrowsAsync<IOException>(() => InstallAsync(fixture));

        // Assert
        Assert.Equal(after, File.Exists(fixture.Local.PathFor(stage)));

        // Arrange
        fixture.Reopen();
        if (stage != CONFIRMATION || !after)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(fixture));
            Assert.Empty(fixture.Local.Files.Writes);
        }

        // Act
        LocalVoterCredentialInstallation installed = await InstallAsync(fixture);

        // Assert
        Assert.Equal(installed, await ReadAsync(fixture));
        Assert.Equal(fixture.Result.CredentialRevision, installed.CredentialRevision);
        Assert.Equal(fixture.Local.Reference, installed.MaterialReference);
        Assert.Equal(fixture.Local.Artifact!.IssueId, installed.IssueId);
        Assert.Equal(fixture.Local.Artifact.CertificateDigest, installed.CertificateFingerprint);
        Assert.Equal(keyBundle, File.ReadAllBytes(fixture.Local.PathFor("intent")));
        Assert.Equal(artifact, File.ReadAllBytes(fixture.Local.PathFor("artifact")));
        byte[] previousKey = fixture.Local.Secrets.Read(fixture.Local.PreviousKeyOwner, "peer", fixture.Local.PreviousKeyReference);
        try
        {
            Assert.True(previousKey.Length > 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousKey);
        }
        AssertOnlyOriginalIssuance(fixture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MANIFEST)]
    [InlineData(REFERENCE)]
    [InlineData(CONFIRMATION)]
    public async Task MajorityLossBeforeFirstOrAfterAnyWrite_BlocksUntilFreshRecovery(string? stage)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        VoterCredentialAuthorityFixture.Barrier barrier = fixture.Local.Authority.SubjectBarrier;
        CapturedLog logs = new();
        int calls = barrier.Calls;
        int boundary = Array.IndexOf(new[] { MANIFEST, REFERENCE, CONFIRMATION }, stage) + 2;
        barrier.After = observed =>
        {
            if (observed == calls + boundary)
            {
                fixture.Local.Authority.Subject.Invalidate();
            }
        };

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallAsync(fixture, logs));

        // Assert
        Assert.Empty(logs.Messages);
        if (stage is not null)
        {
            Assert.True(File.Exists(fixture.Local.PathFor(stage)));
        }

        // Arrange
        fixture.Reopen();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadAsync(fixture));
        barrier.After = null;
        Assert.True(fixture.Local.Authority.Subject.ConfirmRecovery(fixture.Local.Authority.Scenario.State,
            fixture.Local.Authority.Subject.RecoveryVersion));

        // Act
        LocalVoterCredentialInstallation installed = await InstallAsync(fixture);

        // Assert
        Assert.Equal(installed, await ReadAsync(fixture));
        AssertOnlyOriginalIssuance(fixture);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("bind-result")]
    public async Task MaterialRemovedWhileConfirming_RejectsCachedEvidence(string stage)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        VoterCredentialAuthorityFixture.Barrier barrier = fixture.Local.Authority.SubjectBarrier;
        int confirmationRead = barrier.Calls + 4;
        barrier.After = observed =>
        {
            if (observed == confirmationRead)
            {
                File.Delete(fixture.Local.PathFor(stage));
            }
        };
        CapturedLog logs = new();

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(fixture, logs));
        fixture.Reopen();
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(fixture));

        // Assert
        Assert.True(File.Exists(fixture.Local.PathFor(CONFIRMATION)));
        Assert.Empty(logs.Messages);
        AssertOnlyOriginalIssuance(fixture);
    }

    [Theory]
    [InlineData(MANIFEST, false)]
    [InlineData(MANIFEST, true)]
    [InlineData(REFERENCE, false)]
    [InlineData(REFERENCE, true)]
    [InlineData(CONFIRMATION, true)]
    [InlineData("bind-result", false)]
    [InlineData("bind-result", true)]
    [InlineData("bind-intent", false)]
    [InlineData("artifact", false)]
    [InlineData("artifact", true)]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    public async Task MissingPredecessorOrCorruptFile_BlocksBothRecoveryAndRead(string stage, bool corrupt)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        await InstallAsync(fixture);
        File.Delete(fixture.Local.PathFor(stage));
        if (corrupt)
        {
            File.WriteAllText(fixture.Local.PathFor(stage), "invalid envelope");
        }

        fixture.Reopen();

        // Act
        Exception? install = await Record.ExceptionAsync(() => InstallAsync(fixture));
        Exception? read = await Record.ExceptionAsync(() => ReadAsync(fixture));

        // Assert
        Assert.NotNull(install);
        Assert.NotNull(read);
        Assert.Empty(fixture.Local.Files.Writes);
        if (stage == "bind-result" && !corrupt)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.BindAsync());
            Assert.False(File.Exists(fixture.Local.PathFor(stage)));
        }

        AssertOnlyOriginalIssuance(fixture);
    }

    [Theory]
    [InlineData("local-key")]
    [InlineData("local-result")]
    [InlineData("accepted-result")]
    [InlineData("manifest")]
    [InlineData("reference")]
    [InlineData("confirmation")]
    public async Task AuthenticatedSubstitution_CannotChangeInstalledCredential(string entry)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        await InstallAsync(fixture);
        RevisionImage bound = fixture.Local.Authority.Scenario.State;
        switch (entry)
        {
            case "local-key":
                fixture.Local.RewriteIntent(intent => intent, replaceKey: true);
                break;
            case "local-result":
                fixture.Rewrite("bind-result", fixture.Result with { Result = fixture.Result.Result with { Index = bound.Index + 1 } });
                break;
            case "accepted-result":
                CommandResult result = fixture.Result.Result with { Term = fixture.Result.Result.Term + 1 };
                fixture.Rewrite("bind-result", fixture.Result with { Result = result });
                fixture.Local.Authority.Scenario.State = bound with
                {
                    Term = result.Term,
                    Results = bound.Results.SetItem(result.OperationId, result)
                };
                fixture.Local.Authority.Publish();
                break;
            case "manifest":
                fixture.Rewrite(MANIFEST, ReadRecord<LocalVoterCredentialManifest>(fixture, MANIFEST) with
                { MaterialReference = new string('F', 64) });
                break;
            case "reference":
                fixture.Rewrite(REFERENCE, ReadRecord<LocalVoterCredentialInstalledReference>(fixture, REFERENCE) with
                { CredentialRevision = bound.Index + 1 });
                break;
            default:
                fixture.Rewrite(CONFIRMATION, ReadRecord<LocalVoterCredentialConfirmation>(fixture, CONFIRMATION) with
                { ReferenceDigest = new string('F', 64) });
                break;
        }

        fixture.Reopen();

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(fixture));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(fixture));

        // Assert
        Assert.Empty(fixture.Local.Files.Writes);
        AssertOnlyOriginalIssuance(fixture);
    }

    [Theory]
    [InlineData("pre-bind")]
    [InlineData("pre-retire")]
    [InlineData("same-index-fork")]
    [InlineData("local-slot")]
    public async Task StaleOrConflictingCutsAndLocalRollback_CloseContinuation(string rollback)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        RevisionImage authorized = fixture.Local.Authority.Scenario.State;
        await fixture.BindAsync();
        RevisionImage bound = fixture.Local.Authority.Scenario.State;
        fixture.Retire();
        RevisionImage retired = fixture.Local.Authority.Scenario.State;
        await InstallAsync(fixture);
        if (rollback == "local-slot")
        {
            foreach (string stage in new[] { "bind-intent", "bind-result", MANIFEST, REFERENCE, CONFIRMATION })
            {
                File.Delete(fixture.Local.PathFor(stage));
            }
        }

        fixture.Local.Authority.Scenario.State = rollback switch
        {
            "pre-bind" => authorized,
            "pre-retire" => bound,
            "same-index-fork" => retired with { Digest = new string('F', 64) },
            _ => retired
        };
        fixture.Local.Authority.Publish();
        fixture.Reopen();

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallAsync(fixture));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(fixture));

        // Assert
        Assert.Empty(fixture.Local.Files.Writes);
        AssertOnlyOriginalIssuance(fixture);
    }

    [Fact]
    public async Task ConcurrentInstallationsAfterRetirement_UseOneProtectedRecordChain()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await BindAsync(fixture);
        fixture.Retire();
        fixture.Local.Authority.Time.Now = fixture.Local.Authority.Time.Now.AddDays(8);
        fixture.Local.Authority.Remote.Available = false;
        fixture.Reopen();
        CapturedLog logs = new();
        await fixture.Local.State.Transition.WaitAsync(fixture.Token);
        Task<LocalVoterCredentialInstallation>[] pending;
        try
        {
            pending = Enumerable.Range(0, 6).Select(_ => InstallAsync(fixture, logs)).ToArray();
            Assert.All(pending, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            fixture.Local.State.Transition.Release();
        }

        // Act
        LocalVoterCredentialInstallation[] results = await Task.WhenAll(pending);

        // Assert
        Assert.Equal(new[] { MANIFEST, REFERENCE, CONFIRMATION }, fixture.Local.Files.Writes);

        // Arrange
        fixture.Reopen();

        // Act
        LocalVoterCredentialInstallation installed = await ReadAsync(fixture);

        // Assert
        Assert.All(results, result => Assert.Equal(installed, result));
        Assert.Empty(fixture.Local.Files.Writes);
        string message = Assert.Single(logs.Messages);
        Assert.Contains(installed.IssueId.ToString(), message);
        Assert.DoesNotContain(fixture.Local.Reference, message);
        Assert.DoesNotContain("Ready", message);
        AssertOnlyOriginalIssuance(fixture);
    }

    private static async Task BindAsync(LocalVoterCredentialBindingFixture fixture)
    {
        await fixture.PrepareAsync();
        await fixture.BindAsync();
    }

    private static T ReadRecord<T>(LocalVoterCredentialBindingFixture fixture, string stage)
    {
        return StrictOnboardingJson.Decode<T>(fixture.Local.Files.Read(fixture.Local.Reference, stage)!);
    }

    private static Task<LocalVoterCredentialInstallation> InstallAsync(LocalVoterCredentialBindingFixture fixture,
        ILogger<LocalVoterCredentialInstallationCoordinator>? logger = null)
    {
        LocalVoterCredentialInstallationCoordinator coordinator = new(fixture.Coordinator, fixture.Local.Files,
            fixture.Local.State, logger ?? NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
        return coordinator.InstallAsync(fixture.Local.Reference, fixture.Token);
    }

    private static Task<LocalVoterCredentialInstallation> ReadAsync(LocalVoterCredentialBindingFixture fixture)
    {
        LocalVoterCredentialInstallationCoordinator coordinator = new(fixture.Coordinator, fixture.Local.Files,
            fixture.Local.State, NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
        return coordinator.ReadInstalledAsync(fixture.Local.Reference, fixture.Token);
    }

    private static void AssertOnlyOriginalIssuance(LocalVoterCredentialBindingFixture fixture)
    {
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
        Assert.Null(fixture.Local.State.Current);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    private sealed class CapturedLog : ILogger<LocalVoterCredentialInstallationCoordinator>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
