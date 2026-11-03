using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class EnrollmentClientTests
{
    [Fact]
    public async Task LostReserveResponseAndNewProcessKeepIdentityDerAndWaitForAcceptedLease()
    {
        // Arrange
        await using ManagedBootstrapFixture first = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Guid code = InvitationSecrets.CreateCode();
        await EnrollmentGatewayTests.InviteAsync(first, Guid.NewGuid(), code, token);
        InstallationOptions options = new()
        {
            InstallationIntent = "Setup", StateDirectory = Path.Combine(first.Root, "join-state"),
            KeyDirectory = Path.Combine(first.Root, "join-keys"), SecretDirectory = Path.Combine(first.Root, "join-secret"),
            BrowserOrigin = "https://joining-browser.example.test", PeerEndpoint = "https://joining-peer.example.test:8443/internal/raft"
        };
        using InstallationLease lease = new(options, Path.Combine(first.Root, "join-storage"));
        LocalSecretStore secrets = new(Options.Create(options), lease);
        Guid attemptId = Guid.NewGuid();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pem = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
        string reference = secrets.Write(attemptId, "peer", pem);
        CryptographicOperations.ZeroMemory(pem);
        InstallationState installation = new();
        LocalInstallationProfile profile = new(lease.InstallIdentity, "Join", InstallationPhase.LocalPrepared,
            3, 2, 1, lease.BootId, first.Time.Now.AddMinutes(2))
        {
            AttemptId = attemptId, NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
            KeyReference = reference, PeerAddress = options.PeerEndpoint,
            OperationIds = new Dictionary<string, Guid> { ["reserve"] = Guid.NewGuid(), ["admit"] = Guid.NewGuid() }.ToImmutableDictionary()
        };
        installation.Publish(profile);
        EnrollmentClientStore store = new();
        OnboardingProgress progress = new(store, secrets);
        EnrollmentLoopbackHandler handler = new(first) { LoseReserveResponse = true };
        handler.BeforeIssue = () => Assert.Null(store.Current!.ProtectedInvitation);
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(options));
        using EnrollmentTransport transport = new(handler, endpoints);
        JoinEnrollmentPreparation preparation = new(store, installation, secrets, progress, transport, endpoints);
        EnrollmentTrustInstallation trust = new(Options.Create(options), lease, secrets, store, first.Time);
        EnrollmentProbeSession session = new(installation, lease, first.Time);
        EnrollmentPeerProbes? firstProbes = null;
        EnrollmentPeerProbes? joiningProbes = null;
        using EnrollmentProbeHandler probeHandler = new(probe =>
        {
            EnrollmentDescriptor descriptor = store.Current!.Descriptor!;
            bool forward = probe.ToNodeId == first.Options.NodeId;
            var serverCertificate = forward ? first.Tls.Certificate! : session.Certificate!;
            Assert.True(EnrollmentPeerProbeTransport.ValidateServer(serverCertificate,
                System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
                EnrollmentProbeSession.Registry(descriptor), probe.ToNodeId, first.Time.Now));
            return forward ? firstProbes!.Reply(probe, session.Certificate!) : joiningProbes!.Reply(probe, first.Tls.Certificate!);
        });
        EnrollmentPeerProbeTransport probeTransport = new(Options.Create(options), probeHandler);
        firstProbes = new(probeTransport, new(first.Installation, first.Lease, first.Time), first.Tls,
            first.Descriptors, new(first.Time), Options.Create(first.Local), first.Time);
        joiningProbes = new(probeTransport, session, new(installation, probes: session), first.Descriptors,
            new(first.Time), Options.Create(options), first.Time);
        handler.Probes = firstProbes;
        JoinEnrollmentAuthority authority = new(store, transport, secrets, progress, installation, lease, endpoints, first.Time, trust, joiningProbes);
        LocalOnboardingAttempt original = await preparation.PrepareAsync("https://browser.example.test/", code.ToString("D"), token);

        // Act
        Exception? lost = await Record.ExceptionAsync(() => authority.PrepareAsync(original, token));

        // Assert
        Assert.IsType<HttpRequestException>(lost);
        Assert.NotNull(store.Current!.ProtectedInvitation);
        Assert.Null(store.Current.ReservationIndex);
        Assert.NotNull(first.Accepted.Current!.Onboarding!.Invitations[original.Request.InvitationId].Attempt);

        // Act
        LocalOnboardingAttempt saved = await preparation.PrepareAsync("https://browser.example.test/", null, token);
        ServiceResult<SetupAuthorityEvidence> result = await authority.PrepareAsync(saved, token);

        // Assert
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(attemptId, result.Data!.AttemptId);
        Assert.Equal(original.Request, store.Current.Request);
        Assert.Equal(original.PrivateKeyReference, store.Current.PrivateKeyReference);
        Assert.Equal(original.RequestDigest, store.Current.RequestDigest);
        Assert.Equal(original.CsrDer, store.Current.CsrDer);
        Assert.Null(store.Current.ProtectedInvitation);
        Assert.NotNull(store.Current.ReservationIndex);
        Assert.Equal("Admitted", result.Data.Enrollment!.Status);
        Assert.NotNull(result.Data.Enrollment.CertificateDer);
        Assert.All(handler.Origins, origin => Assert.Equal("https://browser.example.test", origin));
        Assert.False(installation.CommonOpen);
        Assert.Equal(InstallationPhase.LocalPrepared, installation.Current!.Phase);
        Assert.False(first.Accepted.Current.Operations.Nodes[profile.NodeId!.Value].Voting);
        Assert.False(first.Accepted.Current.Operations.Nodes[profile.NodeId.Value].Activated);
        Assert.Equal(2, probeHandler.Calls);
        Assert.Equal(lease.BootChallenge, store.Current.PeerProbes!.BootChallenge);

        // Assert: durable trust reference, повреждение/незавершённый file effect/orphan не дают credentials.
        Assert.Equal(store.Current.TrustReference, result.Data.TrustReference);
        Assert.NotNull(store.Current.TrustManifest);
        using (var installed = trust.Read(store.Current))
        {
            Assert.True(installed.HasPrivateKey);
            Assert.Equal(result.Data.Enrollment.CertificateDer, Convert.ToBase64String(installed.RawData));
        }

        string trustPath = Path.Combine(options.StateDirectory, "enrollment-trust", store.Current.TrustReference + ".trust");
        byte[] trustBytes = File.ReadAllBytes(trustPath);
        File.WriteAllBytes(trustPath, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => trust.Read(store.Current));
        File.Delete(trustPath);
        Assert.Throws<FileNotFoundException>(() => trust.Read(store.Current));
        await trust.InstallAsync(store.Current, token);
        Assert.Equal(trustBytes, File.ReadAllBytes(trustPath));
        string orphan = Path.Combine(options.StateDirectory, "enrollment-trust", "orphan.trust");
        ProtectedInstallationFiles.WriteNew(orphan, trustBytes);
        Assert.Throws<InvalidDataException>(() => trust.Reconcile(profile, store.Current));
        File.Delete(orphan);
        trust.Reconcile(profile, store.Current);
        Assert.Throws<InvalidDataException>(() => trust.Read(store.Current with { TrustReference = new string('0', 64) }));
        EnrollmentDescriptor withoutNode = store.Current.Descriptor! with
        {
            Nodes = store.Current.Descriptor.Nodes.Where(node => node.NodeId != profile.NodeId).ToArray()
        };
        Assert.Throws<InvalidOperationException>(() => trust.Read(store.Current with { Descriptor = withoutNode }));

        // Arrange
        EnrollmentDescriptor admitted = store.Current.Descriptor!;
        lease.Dispose();
        using InstallationLease restartedLease = new(options, Path.Combine(first.Root, "join-storage"));
        installation.Publish(profile with { BootId = restartedLease.BootId, RuntimeGeneration = profile.RuntimeGeneration + 1,
            ProfileRevision = profile.ProfileRevision + 1 });
        EnrollmentTrustInstallation restartedTrust = new(Options.Create(options), restartedLease, secrets, store, first.Time);
        session = new(installation, restartedLease, first.Time);
        joiningProbes = new(probeTransport, session, new(installation, probes: session), first.Descriptors,
            new(first.Time), Options.Create(options), first.Time);
        JoinEnrollmentAuthority restarted = new(store, transport, secrets, progress, installation, restartedLease, endpoints, first.Time, restartedTrust, joiningProbes);

        // Act
        ServiceResult<SetupAuthorityEvidence> early = await restarted.PrepareAsync(store.Current, token);

        // Assert
        Assert.False(early.Success);
        Assert.Equal("PreviousProcessLeaseActive", early.Error!.Message);
        Assert.Equal(admitted.ProcessEpoch, store.Current.Descriptor!.ProcessEpoch);
        Assert.False(installation.CommonOpen);

        // Act
        foreach (int _ in Enumerable.Range(0, 5))
        {
            first.Time.Now = first.Time.Now.AddSeconds(30);
            await first.RefreshAsync(token);
            await first.Driver.TickAsync(token);
        }

        ServiceResult<SetupAuthorityEvidence> resumed = await restarted.PrepareAsync(store.Current, token);

        // Assert
        Assert.True(resumed.Success, resumed.Error?.Message);
        Assert.Equal(admitted.ProcessEpoch + 1, resumed.Data!.Enrollment!.ProcessEpoch);
        Assert.Equal(restartedLease.BootId, resumed.Data.Enrollment.BootId);
        Assert.Equal(restartedLease.BootChallenge, resumed.Data.Enrollment.BootChallenge);
        Assert.NotEqual(admitted.BootChallenge, resumed.Data.Enrollment.BootChallenge);
        Assert.Equal(admitted.CertificateDer, resumed.Data.Enrollment.CertificateDer);
        Assert.Equal(admitted.ReservationIndex, resumed.Data.Enrollment.ReservationIndex);
        Assert.Null(store.Current!.ProtectedInvitation);
        Assert.Equal(2, first.Store.Issues.Count);
        Assert.False(installation.CommonOpen);

        // Arrange
        EnrollmentRequest enrollmentRequest = original.Request;
        const string OLD_ACTION = "RenewOnboardingProcess";
        string oldNonce = first.Nonces.Issue(attemptId, lease.BootId, lease.BootChallenge, 1, OLD_ACTION, original.RequestDigest);
        EnrollmentProof oldProof = EnrollmentProofs.Sign(enrollmentRequest,
            new(1, Guid.NewGuid(), OLD_ACTION, lease.BootId, lease.BootChallenge, 1, oldNonce, original.RequestDigest, string.Empty), key);

        // Act
        Exception? replay = await Record.ExceptionAsync(() => first.Enrollment.ExchangeAsync(
            new(1, enrollmentRequest, oldProof, original.CsrDer!, null), token));

        // Assert
        Assert.IsType<InvalidOperationException>(replay);
        Assert.Equal(2, first.Accepted.Current!.Onboarding!.Invitations[original.Request.InvitationId].Attempt!.ProcessEpoch);
        Assert.Equal(restartedLease.BootChallenge, first.Accepted.Current.Onboarding.Invitations[original.Request.InvitationId].Attempt!.BootChallenge);
    }
}
