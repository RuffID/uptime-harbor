using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.Issuers;

public class IssuerRegistrationTests
{
    [Fact]
    public async Task NewDedicatedCaIsAcceptedOnceWithOriginalKeyAndLegacyCredentialsUnchanged()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid id = Guid.NewGuid();
        ClusterOperationsImage operations = fixture.State.Current!.Operations;
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(fixture.Options.ClientKeyPath, "unavailable legacy operator private material");
        using FileStream lockedLegacyKey = new(fixture.Options.ClientKeyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        JsonResult first = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        EnrollmentIssuerReference local = fixture.Signing.Issuers[id];
        byte[] ciphertext = File.ReadAllBytes(Path.Combine(fixture.Local.KeyDirectory, local.KeyReference.Split(':')[0] + ".key"));
        JsonResult repeated = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, repeated.StatusCode);
        Assert.True(Data(first).GetProperty("IssuanceReady").GetBoolean());
        Assert.Equal(Data(first).GetProperty("AcceptedIndex").GetInt64(), Data(repeated).GetProperty("AcceptedIndex").GetInt64());
        Assert.Same(operations, fixture.State.Current!.Operations);
        Assert.Single(fixture.Signing.Issuers);
        Assert.Single(fixture.Projection.Writes);
        Assert.Single(fixture.Work.Items);
        Assert.Single(fixture.Delivery.Sent);
        Assert.Equal(1, fixture.Signing.Commits);
        Assert.Equal(ciphertext, File.ReadAllBytes(Path.Combine(fixture.Local.KeyDirectory, local.KeyReference.Split(':')[0] + ".key")));
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(local.CertificateDer);
        Assert.False(certificate.HasPrivateKey);
        Assert.True(EnrollmentCertificates.ValidateIssuer(certificate, fixture.Options.ClusterId, 1, id, fixture.Time.Now));
        Assert.Equal(256, certificate.GetECDsaPublicKey()!.KeySize);
        Assert.Equal(0, certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().PathLengthConstraint);
        Assert.True(fixture.Native.Calls >= 8);
        Assert.True(fixture.Peer.Calls >= 4);
        KeyValuePair<string, object?>[] logged = Assert.Single(fixture.Audit.Entries);
        Assert.Contains(logged, item => item.Key == "EventName" && Equals(item.Value, "IssuerRegistered"));
        Assert.Contains(logged, item => item.Key == "ActorId" && Equals(item.Value, fixture.Actor));
        Assert.DoesNotContain("test-password", JsonSerializer.Serialize(logged));
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(ciphertext));
    }

    [Fact]
    public async Task PendingAndLostResponseKeepExactCommandKeyAndCommittedResult()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid id = Guid.NewGuid();
        fixture.Delivery.Offline = true;

        // Act
        JsonResult queued = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        string protectedCommand = fixture.Work.Items[id].ProtectedPayload;
        string keyRef = fixture.Signing.Issuers[id].KeyReference;
        fixture.Delivery.Offline = false;
        fixture.Delivery.LoseResponse = true;
        JsonResult lost = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        JsonResult retry = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(202, queued.StatusCode);
        Assert.False(Data(queued).GetProperty("IssuanceReady").GetBoolean());
        Assert.Equal(200, lost.StatusCode);
        Assert.Equal(200, retry.StatusCode);
        Assert.Equal("Committed", Data(lost).GetProperty("OperationStatus").GetString());
        Assert.True(Data(retry).GetProperty("IssuanceReady").GetBoolean());
        Assert.Equal(protectedCommand, fixture.Work.Items[id].ProtectedPayload);
        Assert.Equal(keyRef, fixture.Signing.Issuers[id].KeyReference);
        Assert.Equal(fixture.Delivery.Sent[0], fixture.Delivery.Sent[1]);
        Assert.Single(fixture.Projection.Writes);
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task LocalKeyCannotAuthorizeIssuanceBeforeRegistrationCommit()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Delivery.Offline = true;
        Guid id = Guid.NewGuid();
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        EnrollmentIssuerReference local = fixture.Signing.Issuers[id];
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest csr = new("CN=joining", key, HashAlgorithmName.SHA256);
        byte[] bytes = csr.CreateSigningRequest();
        Guid node = Guid.NewGuid();
        Guid storage = Guid.NewGuid();
        EnrollmentRequest request = new(1, fixture.Options.ClusterId, 1, Guid.NewGuid(), Guid.NewGuid(), node,
            storage, "https://joining.invalid/internal/raft", EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(bytes));
        EnrollmentIssuance template = new(1, Guid.NewGuid(), id, new string('1', 32), node, storage, request.PeerUri,
            request.SpkiDigest, request.CsrDigest, fixture.Time.Now, fixture.Time.Now.AddDays(1));
        EnrollmentSigningAuthorization authority = new(fixture.Handle, fixture.Runtime, fixture.State, fixture.Readiness,
            Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Time);
        EnrollmentSigner signer = new(fixture.Signing, new(fixture.Signing, new()), new(), fixture.Storage,
            fixture.Time, NullLogger<EnrollmentSigner>.Instance, authority);

        // Act
        Exception? denied = await Record.ExceptionAsync(() => signer.IssueAsync(request, template, bytes, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(denied);
        Assert.Null(fixture.State.Current!.OnboardingContracts?.Issuers);
        Assert.Empty(fixture.Projection.Writes);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(local.KeyReference, fixture.Signing.Issuers[id].KeyReference);
    }

    [Fact]
    public async Task RegisteredIssuerSignsOnlyExactAcceptedIssuanceAndStillRequiresFreshMajority()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid id = Guid.NewGuid();
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest csr = new("CN=joining", key, HashAlgorithmName.SHA256);
        byte[] bytes = csr.CreateSigningRequest();
        EnrollmentRequest request = new(1, fixture.Options.ClusterId, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "https://joining.invalid/internal/raft", EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(bytes));
        EnrollmentIssuance template = new(1, Guid.NewGuid(), id, new string('1', 32), request.NodeId, request.StorageGeneration,
            request.PeerUri, request.SpkiDigest, request.CsrDigest, fixture.Time.Now, fixture.Time.Now.AddDays(1));
        EnrollmentSigningAuthorization authority = new(fixture.Handle, fixture.Runtime, fixture.State, fixture.Readiness,
            Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Time);
        EnrollmentSigner signer = new(fixture.Signing, new(fixture.Signing, new()), new(), fixture.Storage,
            fixture.Time, NullLogger<EnrollmentSigner>.Instance, authority);

        // Act
        Exception? noIssuance = await Record.ExceptionAsync(() => signer.IssueAsync(request, template, bytes, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(noIssuance);
        Assert.Empty(fixture.Signing.Issues);

        // Arrange
        RevisionImage image = fixture.State.Current!;
        OnboardingDelegation delegation = new(fixture.Actor, 1, image.Index, fixture.Time.Now);
        OnboardingAttempt attempt = new(request, EnrollmentRequestCodec.Digest(request), Guid.NewGuid(),
            fixture.Time.Now.AddMinutes(30), delegation, Guid.NewGuid(), 1, fixture.Time.Now.AddMinutes(2), fixture.Time.Now)
        {
            BootChallenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Status = OnboardingAttemptStatus.CertificateAuthorized, IssueId = template.IssueId, Issuance = template,
            IssuanceAuthorizationIndex = image.Index
        };
        OnboardingInvitation invitation = new(request.InvitationId, request.InvitationId, new string('A', 64), fixture.Time.Now,
            fixture.Time.Now.AddMinutes(15), delegation) { Status = InvitationStatus.Reserved, Attempt = attempt };
        fixture.State.Publish(image with { Onboarding = new OnboardingImage { Invitations = new OnboardingImage().Invitations.Add(invitation.Id, invitation) } });

        // Act
        EnrollmentIssuedArtifact issued = await signer.IssueAsync(request, template, bytes, TestContext.Current.CancellationToken);
        fixture.Native.FailAt = fixture.Native.Calls + 1;
        Exception? noMajority = await Record.ExceptionAsync(() => signer.IssueAsync(request, template, bytes, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(image.Index, issued.AuthorizationIndex);
        Assert.Equal(issued.CertificateDer, Assert.Single(fixture.Signing.Issues).Value.CertificateDer);
        Assert.IsType<InvalidOperationException>(noMajority);
        Assert.Equal(2, fixture.Signing.Commits);
    }

    [Fact]
    public async Task CommitWithLostPostBarrierRemainsCommittedButDoesNotClaimIssuanceReady()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Native.FailAt = 6;
        Guid id = Guid.NewGuid();

        // Act
        JsonResult result = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        fixture.Native.FailAt = null;
        JsonResult restored = await fixture.Controller.ReadAsync(id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Committed", Data(result).GetProperty("OperationStatus").GetString());
        Assert.False(Data(result).GetProperty("IssuanceReady").GetBoolean());
        Assert.True(Data(restored).GetProperty("IssuanceReady").GetBoolean());
        Assert.Single(fixture.Projection.Writes);
        Assert.Single(fixture.Audit.Entries);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("expired")]
    public async Task AcceptedRejectionIsLoggedWithoutFalseRegisteredEvent(string reason)
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid id = Guid.NewGuid();
        fixture.Delivery.BeforeSend = () =>
        {
            if (reason == "revision")
            {
                fixture.ChangeActor();
            }
            else
            {
                fixture.Time.Now = fixture.Time.Now.AddMinutes(6);
            }
        };

        // Act
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, fixture.State.Current!.Results[id].Outcome);
        Assert.Empty(fixture.State.Current.OnboardingContracts?.Issuers ?? []);
        KeyValuePair<string, object?>[] logged = Assert.Single(fixture.Audit.Entries);
        Assert.Contains(logged, item => item.Key == "EventName" && Equals(item.Value, "IssuerRejected"));
        Assert.DoesNotContain(logged, item => Equals(item.Value, "IssuerRegistered"));
    }

    [Fact]
    public async Task FailedLocalCommitLeavesNoPendingAndOrphanIsNotAdoptedOnRetry()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Signing.FailSave = true;
        Guid id = Guid.NewGuid();

        // Act
        Exception? first = await Record.ExceptionAsync(() => fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken));
        fixture.Signing.FailSave = false;
        Exception? retry = await Record.ExceptionAsync(() => fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<IOException>(first);
        Assert.IsType<InvalidDataException>(retry);
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.Signing.Issuers);
        Assert.Empty(fixture.Projection.Writes);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Single(Directory.GetFiles(fixture.Local.KeyDirectory, "*.key"));
    }

    private static JsonElement Data(JsonResult result)
    {
        return JsonSerializer.SerializeToElement(result.Value).GetProperty("data");
    }
}
