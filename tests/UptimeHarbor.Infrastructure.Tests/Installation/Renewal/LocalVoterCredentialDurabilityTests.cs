using System.Security.Cryptography;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialDurabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntentFault_ReopensOriginalBundleOrLeavesNoExternalEffect(bool after)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        fixture.Files.FailStage = "intent";
        fixture.Files.AfterWrite = after;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.PrepareAsync());
        string? digest = fixture.Files.CapturedIntentDigest;
        string reference = fixture.Reference;
        fixture.Reopen();
        LocalVoterCredentialState resumed = await fixture.PrepareAsync();

        // Assert
        Assert.Equal(reference, resumed.Intent.KeyReference);
        Assert.Empty(fixture.Authority.Journal.Issued);
        Assert.Null(resumed.Authorization);
        Assert.Null(resumed.Artifact);
        Assert.Equal(1, fixture.Authority.Scenario.State.Index);
        if (after)
        {
            Assert.Equal(digest, VoterCredentialProofs.Digest(resumed.Intent.Request));
        }

        LocalVoterCredentialState replay = await fixture.PrepareAsync();
        Assert.Equal(resumed.Intent, replay.Intent);
    }

    [Theory]
    [InlineData("authorization", false)]
    [InlineData("authorization", true)]
    [InlineData("artifact", false)]
    [InlineData("artifact", true)]
    public async Task PinFault_RestartsWithSameIntentAndSingleSignerArtifact(string stage, bool after)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        LocalVoterCredentialState prepared = await fixture.PrepareAsync();
        await fixture.Authority.AuthorizeAsync();
        if (stage == "artifact")
        {
            await fixture.Store.PinAuthorizationAsync(fixture.Reference, fixture.Authority.SigningCall(), fixture.Token);
            await fixture.SignAsync();
        }

        fixture.Files.FailStage = stage;
        fixture.Files.AfterWrite = after;

        // Act
        if (stage == "authorization")
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.PinAuthorizationAsync(fixture.Reference,
                fixture.Authority.SigningCall(), fixture.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.PinAsync());
        }

        fixture.Reopen();
        LocalVoterCredentialState recovered = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);
        await fixture.Store.PinAuthorizationAsync(fixture.Reference, fixture.Authority.SigningCall(), fixture.Token);
        EnrollmentIssuedArtifact issued = await fixture.SignAsync();
        LocalVoterCredentialState pinned = await fixture.PinAsync();

        // Assert
        Assert.Equal(prepared.Intent, recovered.Intent);
        Assert.Equal(prepared.Intent, pinned.Intent);
        Assert.Equal(issued.CertificateDer, pinned.Artifact!.CertificateDer);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Single(fixture.Authority.Scenario.State.Results);
        Assert.Equal(prepared.Intent.Request.OperationId, fixture.Authority.Scenario.State.Results.Single().Key);
        Assert.Equal(after, stage == "artifact" && recovered.Artifact is not null || stage == "authorization" && recovered.Authorization is not null);
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains("Installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostSignerResponse_RereadsIntentAndReplaysOriginalDerBeforePin()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        LocalVoterCredentialIntent original = (await fixture.Store.ReadAsync(fixture.Reference, fixture.Token)).Intent;
        fixture.Authority.Remote.LoseResponse = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.SignAsync());
        byte[] expected = fixture.Authority.Journal.Issued.Single().Value.CertificateDer.ToArray();
        fixture.Reopen();
        fixture.Authority.Remote.LoseResponse = false;
        await fixture.SignAsync();
        LocalVoterCredentialState pinned = await fixture.PinAsync();

        // Assert
        Assert.Equal(original, pinned.Intent);
        Assert.Equal(expected, pinned.Artifact!.CertificateDer);
        Assert.Single(fixture.Authority.Journal.Issued);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(2, fixture.Authority.Scenario.State.Index);
    }

    [Fact]
    public async Task NewAcceptedProcessAfterReopen_UsesSameKeyIntentAndSignerJournal()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        fixture.Authority.Remote.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.SignAsync());
        LocalVoterCredentialIntent original = fixture.Local!.Intent;
        byte[] expected = fixture.Authority.Journal.Issued.Single().Value.CertificateDer.ToArray();
        fixture.Reopen();
        OnboardingContractImage contracts = Assert.IsType<OnboardingContractImage>(fixture.Authority.Scenario.State.OnboardingContracts);
        Assert.NotNull(contracts.OnlineProcesses);
        OnlineProcessLease process = contracts.OnlineProcesses[fixture.Authority.SubjectId];
        process = process with
        {
            BootId = fixture.Lease.BootId, BootChallenge = fixture.Lease.BootChallenge, ProcessEpoch = process.ProcessEpoch + 1
        };
        fixture.Authority.Scenario.State = fixture.Authority.Scenario.State with
        {
            OnboardingContracts = contracts with
            {
                OnlineProcesses = contracts.OnlineProcesses.SetItem(process.NodeId, process)
            }
        };
        fixture.Authority.Publish();
        fixture.Authority.Call = fixture.Authority.Call with
        {
            Proof = fixture.Authority.Call.Proof with
            {
                BootId = process.BootId, BootChallenge = process.BootChallenge, ProcessEpoch = process.ProcessEpoch
            }
        };

        // Act
        await fixture.Authority.PrepareAsync();
        fixture.Authority.Remote.LoseResponse = false;
        await fixture.SignAsync();
        LocalVoterCredentialState pinned = await fixture.PinAsync();

        // Assert
        Assert.Equal(original, pinned.Intent);
        Assert.Equal(expected, pinned.Artifact!.CertificateDer);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(2, fixture.Authority.Scenario.State.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutageOrSignerResponse_HasDurableIntentAndAuthorizationBeforeExternalCall(bool outage)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        byte[] expectedIntent = File.ReadAllBytes(fixture.PathFor("intent"));
        byte[] expectedAuthorization = File.ReadAllBytes(fixture.PathFor("authorization"));
        fixture.Authority.Remote.Available = !outage;
        fixture.Authority.Remote.After = () =>
        {
            Assert.Equal(expectedIntent, File.ReadAllBytes(fixture.PathFor("intent")));
            Assert.Equal(expectedAuthorization, File.ReadAllBytes(fixture.PathFor("authorization")));
            Assert.False(File.Exists(fixture.PathFor("artifact")));
        };

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.SignAsync());
        fixture.Reopen();
        LocalVoterCredentialState resumed = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);

        // Assert
        Assert.Equal(outage, error is not null);
        Assert.NotNull(resumed.Authorization);
        Assert.Null(resumed.Artifact);
        Assert.Equal(expectedIntent, File.ReadAllBytes(fixture.PathFor("intent")));
        Assert.Equal(expectedAuthorization, File.ReadAllBytes(fixture.PathFor("authorization")));
    }

    [Fact]
    public async Task ConcurrentPrepare_WaitsForExistingTransitionAndCreatesOneBundle()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        fixture.Files.BeforeWrite = stage =>
        {
            if (stage == "intent")
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(20), fixture.Token));
            }
        };

        // Act
        Task<LocalVoterCredentialState> first = Task.Run(fixture.PrepareAsync, fixture.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), fixture.Token);
        Task<LocalVoterCredentialState> second = fixture.PrepareAsync();
        bool waiting = !second.IsCompleted;
        release.Set();
        LocalVoterCredentialState[] results = await Task.WhenAll(first, second);

        // Assert
        Assert.True(waiting);
        Assert.Equal(results[0].Intent, results[1].Intent);
        Assert.Equal(new[] { "intent" }, fixture.Files.Writes);
        Assert.Single(fixture.Logs.Messages);
    }

    [Fact]
    public async Task ConcurrentPins_AreImmutableAndLogsAreDeduplicated()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact issued = await fixture.SignAsync();
        byte[] signerBytes = issued.CertificateDer.ToArray();

        // Act
        LocalVoterCredentialState[] states = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(fixture.PinAsync, fixture.Token)));
        issued.CertificateDer[0] ^= 1;
        states[0].Artifact!.CertificateDer[0] ^= 1;
        fixture.Reopen();
        LocalVoterCredentialState read = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);

        // Assert
        Assert.Equal(signerBytes, read.Artifact!.CertificateDer);
        Assert.Equal(3, fixture.Logs.Messages.Count);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(fixture.PathFor("intent"))!).Length);
    }

    [Fact]
    public async Task DurablePins_ContainNoPlaintextPrivateMaterialAndReadCreatesNoLogs()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        await fixture.SignAsync();
        await fixture.PinAsync();
        byte[] bundle = fixture.Files.Read(fixture.Reference, "intent")!;
        string privateText;
        try
        {
            LocalVoterCredentialMaterial.Decode(bundle,
                fixture.Lease.InstallIdentity, fixture.Reference, out ECDsa key);
            using (key)
            {
                byte[] pkcs8 = key.ExportPkcs8PrivateKey();
                try
                {
                    privateText = Convert.ToBase64String(pkcs8);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }

        // Act
        LocalVoterCredentialState read = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);
        string publicJson = Encoding.UTF8.GetString(StrictOnboardingJson.Encode(read));

        // Assert
        Assert.DoesNotContain(privateText, publicJson, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(fixture.PathFor("intent"))!))
        {
            Assert.DoesNotContain(privateText, File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.Equal(3, fixture.Logs.Messages.Count);
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains(privateText, StringComparison.Ordinal)
            || message.Contains(read.Intent.CsrDer, StringComparison.Ordinal)
            || message.Contains(read.Artifact!.CertificateDigest, StringComparison.Ordinal));
    }
}
