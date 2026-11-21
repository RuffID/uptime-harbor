using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedRestartCredentialFailureTests
{
    private static readonly string[] INSTALL_STAGES = ["bind-result", "install-manifest", "install-reference", "install-confirmation"];

    [Theory]
    [InlineData("install-manifest", false)]
    [InlineData("install-reference", false)]
    [InlineData("install-confirmation", true)]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("authorization", false)]
    [InlineData("artifact", false)]
    [InlineData("artifact", true)]
    [InlineData("bind-intent", false)]
    [InlineData("bind-intent", true)]
    [InlineData("bind-result", false)]
    [InlineData("bind-result", true)]
    public async Task IncompleteOrCorruptRotatedSlotCannotStartBeforeMajority(string stage, bool corrupt)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        var installed = await new ManagedRestartRotation(first).InstallAsync(token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        string path = Path.Combine(first.Local.SecretDirectory, "voter-renewal", installed.MaterialReference, stage);
        if (corrupt)
        {
            File.AppendAllText(path, "corrupt");
        }
        else
        {
            File.Delete(path);
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        Assert.NotNull(failure);
        Assert.Null(restart.Tls.Capture());
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Equal(0, restart.Native.Barriers);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("conflicting-head")]
    [InlineData("conflicting-bind-result")]
    [InlineData("expired")]
    [InlineData("revoked-leaf")]
    [InlineData("revoked-issuer")]
    [InlineData("missing-pin")]
    public async Task ChangedProofAtFreshReadCannotWriteBindResultOrOpenWork(string damage)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        ManagedRestartRotation renewal = new(first);
        var interrupted = await renewal.InstallAsync(token, "accepted-bind");
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        await restart.RestoreAsync(token);
        RevisionImage replay = restart.Accepted.Current!;
        Guid issueId = interrupted.IssueId;
        var rotation = replay.OnboardingContracts!.VoterCredentialRotations![issueId];
        Guid bindId = rotation.BindOperationId!.Value;
        Guid issuerEpoch = rotation.Renewal.Issuance.IssuerEpoch;
        var issuer = replay.OnboardingContracts.Issuers![issuerEpoch];
        var credential = replay.Operations.Credentials[issueId];
        string slot = Path.Combine(first.Local.SecretDirectory, "voter-renewal", interrupted.MaterialReference);
        restart.Native.OnReadBarrier = () =>
        {
            switch (damage)
            {
                case "stale":
                    restart.Accepted.Publish(renewal.CutBeforeBind!);
                    break;
                case "conflicting-head":
                    restart.Accepted.Publish(replay with { Digest = new string('0', 64) });
                    break;
                case "conflicting-bind-result":
                    restart.Accepted.Publish(replay with
                    {
                        Results = replay.Results.SetItem(bindId, replay.Results[bindId] with { Term = replay.Term + 1 }),
                        Index = replay.Index + 1,
                        Term = replay.Term + 1
                    });
                    break;
                case "expired":
                    first.Time.Now = credential.ExpiresAtUtc;
                    break;
                case "revoked-leaf":
                    restart.Accepted.Publish(replay with { Operations = replay.Operations with
                    {
                        Credentials = replay.Operations.Credentials.SetItem(issueId, credential with { RevokedAtUtc = first.Time.Now })
                    } });
                    break;
                case "revoked-issuer":
                    restart.Accepted.Publish(replay with { OnboardingContracts = replay.OnboardingContracts with
                    {
                        Issuers = replay.OnboardingContracts.Issuers.SetItem(issuerEpoch, issuer with { RevokedAtUtc = first.Time.Now })
                    } });
                    break;
                case "missing-pin":
                    File.Delete(Path.Combine(slot, "artifact"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(damage));
            }
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.Publication.ConfirmRestartAsync(token));

        // Assert
        Assert.True(failure is InvalidDataException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(1, restart.Native.Barriers);
        Assert.Equal(0, restart.Native.Replications);
        Assert.All(INSTALL_STAGES, stage => Assert.False(File.Exists(Path.Combine(slot, stage))));
        Assert.Null(restart.Tls.Capture());
        Assert.False(restart.Enable.Allows());
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Readiness.CanWork(restart.Runtime));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("authorization")]
    [InlineData("artifact")]
    [InlineData("bind-result")]
    [InlineData("install-confirmation")]
    public async Task InvalidPendingBindPrefixCannotStart(string stage)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        var interrupted = await new ManagedRestartRotation(first).InstallAsync(token, "before-bind");
        string path = Path.Combine(first.Local.SecretDirectory, "voter-renewal", interrupted.MaterialReference, stage);
        if (stage is "bind-result" or "install-confirmation")
        {
            File.Copy(Path.Combine(Path.GetDirectoryName(path)!, "bind-intent"), path);
        }
        else
        {
            File.Delete(path);
        }
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => restart.RestoreAsync(token));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(0, interrupted.CredentialRevision);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.Null(restart.Tls.Capture());
        Assert.False(restart.Installation.CommonOpen);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("digest")]
    [InlineData("bind-result")]
    [InlineData("pin")]
    [InlineData("expiry")]
    [InlineData("leaf-revocation")]
    [InlineData("issuer-revocation")]
    [InlineData("generation")]
    public async Task PendingBindNeedsAnIntactFreshCutAndRuntimeGeneration(string damage)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        ManagedRestartRotation renewal = new(first);
        var interrupted = await renewal.InstallAsync(token, "before-bind");
        if (damage == "expiry")
        {
            var credential = first.Accepted.Current!.Operations.Credentials[first.Store.Profile!.CredentialId!.Value];
            first.Time.Now = credential.ExpiresAtUtc.AddSeconds(-1);
        }

        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        await restart.RestoreAsync(token);

        RevisionImage replay = restart.Accepted.Current!;
        var oldLeaf = replay.Operations.Credentials[first.Store.Profile!.CredentialId!.Value];
        var issuer = Assert.Single(replay.OnboardingContracts!.Issuers!).Value;
        var rotate = replay.OnboardingContracts.VoterCredentialRotations![interrupted.IssueId];
        string slot = Path.Combine(first.Local.SecretDirectory, "voter-renewal", interrupted.MaterialReference);
        bool currentGeneration = true;
        using var generation = new RuntimeGenerationContext(() =>
        {
            if (!currentGeneration)
            {
                throw new OperationCanceledException("TestRuntimeGenerationClosed");
            }
        });
        restart.Native.OnReadBarrier = () =>
        {
            switch (damage)
            {
                case "rollback":
                    restart.Accepted.Publish(replay with { Index = replay.Index - 1 });
                    break;
                case "digest":
                    restart.Accepted.Publish(replay with { Digest = new string('0', 64) });
                    break;
                case "bind-result":
                    Guid bindId = renewal.BindCommand!.OperationId;
                    var conflict = replay.Results[rotate.AuthorizationOperationId] with { OperationId = bindId };
                    restart.Accepted.Publish(replay with
                    {
                        Index = replay.Index + 1, Results = replay.Results.SetItem(bindId, conflict)
                    });
                    break;
                case "pin":
                    File.Delete(Path.Combine(slot, "artifact"));
                    break;
                case "expiry":
                    first.Time.Now = oldLeaf.ExpiresAtUtc;
                    break;
                case "leaf-revocation":
                    restart.Accepted.Publish(replay with { Operations = replay.Operations with
                    {
                        Credentials = replay.Operations.Credentials.SetItem(oldLeaf.Id, oldLeaf with { RevokedAtUtc = first.Time.Now })
                    } });
                    break;
                case "issuer-revocation":
                    restart.Accepted.Publish(replay with { OnboardingContracts = replay.OnboardingContracts with
                    {
                        Issuers = replay.OnboardingContracts.Issuers!.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = first.Time.Now })
                    } });
                    break;
                case "generation":
                    currentGeneration = false;
                    break;
            }
        };

        Assert.NotNull(await Record.ExceptionAsync(() => restart.Driver.TickAsync(token)));

        Assert.True(restart.Tls.Capture() is null or { Quarantined: true });
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Equal(0, restart.Native.Replications);
        Assert.Equal(0, restart.RenewalSends);
        Assert.All(INSTALL_STAGES, stage => Assert.False(File.Exists(Path.Combine(slot, stage))));
    }
}
