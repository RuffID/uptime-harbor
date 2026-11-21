using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedRestartTests
{
    private static readonly string[] INSTALL_STAGES = ["bind-result", "install-manifest", "install-reference", "install-confirmation"];

    [Theory]
    [InlineData("bind-intent", false, false)]
    [InlineData("before-bind", true, false)]
    [InlineData("before-bind", true, true)]
    public async Task RestartWithPendingBindReconcilesBeforeWork(string stopAt, bool remoteCommit, bool afterFirstRead)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        ManagedRestartRotation renewal = new(first);
        var interrupted = await renewal.InstallAsync(token, stopAt);
        Guid oldCredential = first.Store.Profile!.CredentialId!.Value;
        var rotation = first.Accepted.Current!.OnboardingContracts!.VoterCredentialRotations![interrupted.IssueId];
        var authorization = first.Accepted.Current.Results[rotation.AuthorizationOperationId];
        string slot = Path.Combine(first.Local.SecretDirectory, "voter-renewal", interrupted.MaterialReference);
        Dictionary<string, byte[]> pins = new[] { "intent", "authorization", "artifact", "bind-intent" }
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(Path.Combine(slot, stage)));
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        restart.Native.Majority = false;

        await restart.RestoreAsync(token);

        var quarantine = Assert.IsType<DynamicPeerTls.Snapshot>(restart.Tls.Capture());
        Assert.True(quarantine.Quarantined);
        Assert.Equal(oldCredential, quarantine.CredentialId);
        Assert.Equal(0, restart.Native.Barriers);
        await AssertOutboundQuarantineAsync(first, restart, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart.Driver.TickAsync(token));
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Null(await restart.RunScheduledRenewalAsync(token));
        Assert.Equal(pins.Count, Directory.GetFiles(slot).Length);
        Assert.Equal(0, restart.Native.Replications);

        restart.Native.Majority = true;
        restart.Native.RemoteLeader = true;
        await restart.RefreshAsync(token);
        long newTerm = restart.Native.Store.Term + 1;
        restart.Native.OnReadBarrier = () =>
            restart.Native.Store.UpdateTermAsync(newTerm, true, token).AsTask().GetAwaiter().GetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart.Driver.TickAsync(token));
        restart.Native.OnReadBarrier = null;
        Assert.Same(quarantine, restart.Tls.Capture());
        Assert.Equal(pins.Count, Directory.GetFiles(slot).Length);
        Assert.Equal(newTerm, restart.Native.Store.Term);
        await restart.RefreshAsync(token);

        if (!remoteCommit || afterFirstRead)
        {
            await restart.Driver.TickAsync(token);
            Assert.False(restart.Tls.Capture()!.Quarantined);
            Assert.Equal(oldCredential, restart.Tls.Capture()!.CredentialId);
            Assert.Equal(pins.Count, Directory.GetFiles(slot).Length);
        }

        CommandResult? remotelyAccepted = null;
        RevisionCommand original = Assert.IsType<RevisionCommand>(renewal.BindCommand);
        if (remoteCommit)
        {
            ManagedRestartRotation.WalTransport incoming = new(restart.Native, restart.Accepted, first.Time, "accepted-bind");
            await Assert.ThrowsAnyAsync<IOException>(() => incoming.SendAsync([original], token));
            remotelyAccepted = restart.Accepted.Current!.Results[original.OperationId];
            Assert.Equal(RevisionCodec.Digest(original), remotelyAccepted.Digest);
            await restart.Driver.TickAsync(token);
            Assert.Equal(interrupted.IssueId, restart.Tls.Capture()!.CredentialId);
        }

        Assert.False(restart.Tls.IsUsable(quarantine));
        Assert.Equal(InstallationPhase.ClusterQuarantine, restart.Installation.Current!.Phase);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Equal(0, restart.RenewalSends);
        Assert.Equal(0, restart.Native.Replications);

        first.Time.Now = first.Time.Now.AddMinutes(3);
        restart.Native.RemoteLeader = false;
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        Assert.True(restart.Installation.CommonOpen);
        var published = await restart.RunScheduledRenewalAsync(token);
        Assert.Equal(remoteCommit ? 0 : 1, restart.RenewalSends);
        Assert.Equal(!remoteCommit, published is not null);
        Assert.Null(await restart.RunScheduledRenewalAsync(token));
        var accepted = restart.Accepted.Current!;
        var bound = accepted.OnboardingContracts!.VoterCredentialRotations![interrupted.IssueId];
        Assert.Equal(original.OperationId, bound.BindOperationId);
        Assert.Equal(RevisionCodec.Digest(original), bound.BindDigest);
        Assert.Equal(interrupted.IssueId, restart.Tls.Capture()!.CredentialId);
        Assert.Equal(interrupted.CertificateFingerprint, restart.Tls.Capture()!.Fingerprint);
        Assert.Equal(Convert.FromBase64String(accepted.Operations.Credentials[interrupted.IssueId].PublicCertificate),
            restart.Tls.Capture()!.Certificate.RawData);
        Assert.Equal(authorization, accepted.Results[rotation.AuthorizationOperationId]);
        if (remotelyAccepted is not null)
        {
            Assert.Equal(remotelyAccepted, accepted.Results[bound.BindOperationId!.Value]);
        }

        foreach (var (stage, bytes) in pins)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(slot, stage)));
        }
    }

    [Fact]
    public async Task RestartBeforeBindIntentResumesPinnedRotationAfterNormalCommonOpen()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        var interrupted = await new ManagedRestartRotation(first).InstallAsync(token, "before-bind-intent");
        Guid oldCredential = first.Store.Profile!.CredentialId!.Value;
        var authorized = first.Accepted.Current!.OnboardingContracts!.VoterCredentialRotations![interrupted.IssueId];
        var authorizeResult = first.Accepted.Current.Results[authorized.AuthorizationOperationId];
        string slot = Path.Combine(first.Local.SecretDirectory, "voter-renewal", interrupted.MaterialReference);
        Dictionary<string, byte[]> pins = new[] { "intent", "authorization", "artifact" }
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(Path.Combine(slot, stage)));
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        restart.Native.Majority = false;

        // Act
        await restart.RestoreAsync(token);
        DynamicPeerTls.Snapshot? beforeReady = await restart.RunScheduledRenewalAsync(token);

        // Assert
        Assert.False(restart.Installation.CommonOpen);
        Assert.Equal(oldCredential, restart.Tls.Capture()!.CredentialId);
        Assert.Null(beforeReady);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.Equal(0, restart.Native.Replications);
        Assert.Equal(0, restart.RenewalSends);
        Assert.Equal(pins.Count, Directory.GetFiles(slot).Length);

        // Act
        first.Time.Now = first.Time.Now.AddMinutes(3);
        restart.Native.Majority = true;
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        // Assert
        Assert.True(restart.Installation.CommonOpen);

        // Act
        DynamicPeerTls.Snapshot? published = await restart.RunScheduledRenewalAsync(token);
        DynamicPeerTls.Snapshot? next = await restart.RunScheduledRenewalAsync(token);

        // Assert
        DynamicPeerTls.Snapshot installed = Assert.IsType<DynamicPeerTls.Snapshot>(published);
        Assert.Same(installed, restart.Tls.Capture());
        Assert.Equal(interrupted.IssueId, installed.CredentialId);
        Assert.Equal(interrupted.CertificateFingerprint, installed.Fingerprint);
        Assert.Equal(1, restart.RenewalSends);
        Assert.Equal(authorizeResult, restart.Accepted.Current!.Results[authorized.AuthorizationOperationId]);
        Assert.Null(next);
        foreach (var (stage, bytes) in pins)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(slot, stage)));
        }
    }

    [Theory]
    [InlineData(null, false, 4)]
    [InlineData(null, true, 4)]
    [InlineData("accepted-bind", true, 0)]
    [InlineData("bind-result", true, 1)]
    [InlineData("install-manifest", true, 2)]
    [InlineData("install-reference", true, 3)]
    [InlineData("install-confirmation", true, 4)]
    public async Task RotatedSlotRestartsBeforeMajorityAndOpensWithNewCredentialInTwoProcesses(
        string? interruptAfter, bool bindAfterSeal, int durableCount)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        OnlineGrant oldEnable = first.Enable.Current!;
        string sealPath = Path.Combine(first.Local.StateDirectory, "managed-storage.seal");
        string heldSeal = sealPath + ".test-held";
        if (bindAfterSeal)
        {
            // Hold the first checkpoint and prevent the fixture from compacting the WAL tail.
            File.Move(sealPath, heldSeal);
        }

        var installed = await new ManagedRestartRotation(first).InstallAsync(token, interruptAfter);
        Guid originalCredential = first.Store.Profile!.CredentialId!.Value;
        RevisionImage bound = first.Accepted.Current!;
        var rotation = bound.OnboardingContracts!.VoterCredentialRotations![installed.IssueId];
        byte[] der = Convert.FromBase64String(bound.Operations.Credentials[installed.IssueId].PublicCertificate);
        string slot = Path.Combine(first.Local.SecretDirectory, "voter-renewal", installed.MaterialReference);
        string[] pins = ["intent", "authorization", "artifact", "bind-intent"];
        Dictionary<string, byte[]> saved = pins.Concat(INSTALL_STAGES.Take(durableCount))
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(Path.Combine(slot, stage)));
        await first.StopStorageAsync();
        first.Store.RoundTripProfile = true;
        if (bindAfterSeal)
        {
            File.Move(heldSeal, sealPath);
        }

        Guid? priorBoot = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using ManagedRestartFixture restart = new(first);
            restart.Native.Majority = false;

            // Act
            await restart.RestoreAsync(token);

            // Assert
            var quarantine = Assert.IsType<DynamicPeerTls.Snapshot>(restart.Tls.Capture());
            Assert.True(quarantine.Quarantined);
            Assert.Equal(installed.IssueId, quarantine.CredentialId);
            Assert.Equal(installed.CredentialRevision, quarantine.CredentialRevision);
            Assert.Equal(installed.CertificateFingerprint, quarantine.Fingerprint);
            Assert.Equal(der, quarantine.Certificate.RawData);
            Assert.Equal(0, restart.Native.Barriers);
            Assert.NotEqual(priorBoot, restart.Lease.BootId);
            Assert.Equal(originalCredential, restart.Installation.Current!.CredentialId);
            Assert.False(restart.Installation.CommonOpen);
            Assert.False(restart.Readiness.CanWork(restart.Runtime));
            Assert.False(restart.Enable.Confirm(oldEnable));
            await AssertOutboundQuarantineAsync(first, restart, token);
            Assert.IsType<InvalidOperationException>(await Record.ExceptionAsync(() => restart.Driver.TickAsync(token)));
            Assert.IsType<InvalidOperationException>(await Record.ExceptionAsync(() => restart.Publication.ConfirmRestartAsync(token)));
            Assert.Same(quarantine, restart.Tls.Capture());
            Assert.Equal(saved.Count, Directory.GetFiles(slot).Length);
            Assert.Equal(0, restart.Native.Replications);

            // Act
            first.Time.Now = first.Time.Now.AddMinutes(3);
            restart.Native.Majority = true;
            restart.Native.RemoteLeader = true;
            await restart.RefreshAsync(token);
            await restart.Driver.TickAsync(token);

            // Assert
            Assert.False(restart.Tls.Capture()!.Quarantined);
            Assert.Equal(InstallationPhase.ClusterQuarantine, restart.Installation.Current!.Phase);
            Assert.False(restart.Installation.CommonOpen);
            Assert.False(restart.Enable.Allows());
            Assert.Equal(0, restart.Native.Replications);

            // Act
            restart.Native.RemoteLeader = false;
            await restart.RefreshAsync(token);
            for (int step = 0; step < 5; step++)
            {
                await restart.Driver.TickAsync(token);
            }

            // Assert
            var working = restart.Tls.Capture()!;
            Assert.False(working.Quarantined);
            Assert.False(restart.Tls.IsUsable(quarantine));
            Assert.Equal(installed.CredentialRevision, working.CredentialRevision);
            Assert.Equal(der, working.Certificate.RawData);
            Assert.Equal(rotation, restart.Accepted.Current!.OnboardingContracts!.VoterCredentialRotations![installed.IssueId]);
            foreach (Guid operationId in new[] { rotation.AuthorizationOperationId, rotation.BindOperationId!.Value })
            {
                Assert.Equal(bound.Results[operationId], restart.Accepted.Current.Results[operationId]);
            }

            Assert.True(await restart.Publication.ConfirmRestartAsync(token));
            Assert.Same(working, restart.Tls.Capture());
            foreach (var (stage, bytes) in saved)
            {
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(slot, stage)));
            }

            foreach (string stage in INSTALL_STAGES.Skip(durableCount))
            {
                saved.Add(stage, File.ReadAllBytes(Path.Combine(slot, stage)));
            }

            durableCount = INSTALL_STAGES.Length;
            Assert.True(restart.Installation.CommonOpen);
            Assert.True(restart.Readiness.CanWork(restart.Runtime));
            Assert.Equal(installed.IssueId, restart.Enable.Current!.CredentialId);
            Assert.Equal(restart.Lease.BootChallenge, restart.Enable.Current.BootChallenge);
            OnlineGrant unsigned = restart.Enable.Current! with { GrantId = Guid.NewGuid(), Signature = string.Empty };
            var signedRequest = await restart.GrantRequests.PrepareAsync(unsigned, token);
            Assert.True(OnlineGrantRequestSignatures.Verify(restart.Accepted.Current!, signedRequest, first.Time.Now));
            Assert.Equal(signedRequest, await restart.GrantRequests.PrepareAsync(unsigned, token));
            first.Time.Now = first.Time.Now.AddSeconds(70);
            await restart.RefreshAsync(token);
            await restart.Driver.TickAsync(token);
            var contracts = restart.Accepted.Current!.OnboardingContracts!;
            var renewal = contracts.OnlineGrants![contracts.OnlineProcesses![first.Options.NodeId].GrantId];
            Assert.True(renewal.ProcessRenewal);
            Assert.Equal(installed.IssueId, renewal.Grant.CredentialId);
            priorBoot = restart.Lease.BootId;
            first.Accepted.Publish(restart.Accepted.Current!);
            first.Time.Now = first.Time.Now.AddDays(3); // The second process cannot use the expired original leaf.
        }
    }

    private static async Task AssertOutboundQuarantineAsync(ManagedBootstrapFixture first, ManagedRestartFixture restart,
        CancellationToken token)
    {
        var trust = new AcceptedPeerTrustSource(restart.Accepted, first.Time);
        var probe = new NativeDispatchProbe();
        using PeerClientHandler handler = new(Options.Create(first.Options), trust, first.Time, restart.Runtime,
            Options.Create(first.Local), restart.Tls) { InnerHandler = probe };
        using HttpMessageInvoker client = new(handler, disposeHandler: false);
        Uri peer = new(first.Options.PublicUri);
        using HttpRequestMessage command = new(HttpMethod.Post, new Uri(peer, "/internal/cluster/v1/commands"));
        using HttpRequestMessage native = new(HttpMethod.Post, peer);

        var rejected = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(command, token));
        Assert.Equal("Peer credential is limited to native quarantine.", rejected.Message);
        Assert.Null(probe.Envelope);

        var dispatched = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(native, token));
        Assert.Equal("TestNativeDispatch", dispatched.Message);
        Assert.Equal(restart.Tls.Capture()!.CredentialId, probe.Envelope!.CredentialId);
    }

    private sealed class NativeDispatchProbe : HttpMessageHandler
    {
        public PeerEnvelope? Envelope { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Envelope = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single());
            throw new InvalidOperationException("TestNativeDispatch");
        }
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Bootstrap")]
    [InlineData("Join")]
    [InlineData("")]
    [InlineData("restart")]
    [InlineData("Restart ")]
    [InlineData("Unknown")]
    public async Task NonRestartModesRejectBeforeAdmissionTlsOrStorageAccess(string mode)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        first.Options.Mode = mode;
        LocalInstallationProfile profile = restart.Installation.Current!;
        string sealPath = Path.Combine(first.Local.StateDirectory, "managed-storage.seal");
        string manifestPath = Path.Combine(first.Options.StoragePath, "manifest.json");
        byte[] originalSeal = File.ReadAllBytes(sealPath);
        byte[] originalManifest = File.ReadAllBytes(manifestPath);
        string[] originalFiles = Directory.GetFiles(first.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();

        // Act
        bool prepared = await restart.Preparation.PrepareAsync(profile, token);

        // Assert
        Assert.False(prepared);
        Assert.Null(restart.Admission.Read());
        Assert.Null(restart.Tls.Certificate);
        Assert.Same(profile, restart.Installation.Current);
        Assert.Same(profile, first.Store.Profile);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.Null(restart.Native.Store);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Equal(originalSeal, File.ReadAllBytes(sealPath));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(originalFiles, Directory.GetFiles(first.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray());

        // Arrange
        File.Delete(sealPath);

        // Act
        bool missingStorage = await restart.Preparation.PrepareAsync(profile, token);

        // Assert
        Assert.False(missingStorage);
        Assert.Null(restart.Admission.Read());
        Assert.Null(restart.Tls.Certificate);
        Assert.False(File.Exists(sealPath));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("seal")]
    [InlineData("missing-wal")]
    [InlineData("maintenance")]
    [InlineData("unknown-write")]
    public async Task DamagedOrOfflineStorageCannotBecomeManagedRestart(string damage)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        string storage = first.Options.StoragePath;
        switch (damage)
        {
            case "manifest":
                File.AppendAllText(Path.Combine(storage, "manifest.json"), " ");
                break;
            case "seal":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "managed-storage.seal"), "corrupt");
                break;
            case "missing-wal":
                File.Delete(Path.Combine(storage, "wal", "state"));
                break;
            case "maintenance":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "maintenance"), "offline");
                break;
            case "unknown-write":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "managed-storage.seal.next"), "unknown");
                break;
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.Preparation.PrepareAsync(restart.Installation.Current!, token));

        // Assert
        Assert.NotNull(failure);
        Assert.Null(restart.Admission.Read());
        Assert.Equal(0, restart.Native.Barriers);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
    }

    [Fact]
    public async Task WholeSealedStorageRestoresBeforeMajorityThenNeedsNewProcessAndEnable()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Assert.True(first.Seals!.Exists);
        OnlineGrant old = first.Enable.Current!;
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        restart.Native.Majority = false;

        // Act
        await restart.RestoreAsync(token);

        // Assert
        Assert.IsType<ManagedRestartProof>(restart.Admission.Read()!.Proof);
        Assert.NotNull(restart.Tls.Certificate);
        Assert.False(restart.Admission.Read()!.FencingConfirmed);
        Assert.Null(restart.Admission.Read()!.BootstrapAccount);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.True(restart.Runtime.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Confirm(old));
        Assert.NotEqual(old.BootChallenge, restart.Lease.BootChallenge);

        // Act
        Exception? unavailable = await Record.ExceptionAsync(() => restart.Driver.TickAsync(token));
        restart.Native.Majority = true;
        first.Time.Now = first.Time.Now.AddMinutes(3);
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        // Assert
        Assert.IsType<InvalidOperationException>(unavailable);
        Assert.Equal(InstallationPhase.ClusterReady, restart.Installation.Current!.Phase);
        Assert.True(restart.Installation.CommonOpen);
        Assert.True(restart.Enable.Allows());
        Assert.Equal(old.ProcessEpoch + 1, restart.Enable.Current!.ProcessEpoch);
        Assert.Equal(restart.Lease.BootChallenge, restart.Enable.Current.BootChallenge);
        Assert.NotEqual(old.GrantId, restart.Enable.Current.GrantId);
        Assert.True(first.Store.Account.Disabled);
        Assert.Single(restart.Accepted.Current!.Values);
    }
}
