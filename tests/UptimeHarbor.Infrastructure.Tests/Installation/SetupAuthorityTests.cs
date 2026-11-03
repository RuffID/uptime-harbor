using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupAuthorityTests
{
    [Fact]
    public async Task LostAuthorityReplyKeepsDurableRequestAndOriginalRevisionsBeforeRetry()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        LocalInstallationProfile prepared = fixture.Store.Profile! with
        {
            Mode = "Bootstrap", Phase = InstallationPhase.LocalPrepared, AttemptId = Guid.NewGuid(), ProfileRevision = 2,
            NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(), ClusterId = Guid.NewGuid(),
            PeerAddress = fixture.Options.PeerEndpoint, BootstrapAccountReference = "local-reference",
            BootstrapAccountDigest = new string('A', 64),
            OperationIds = new Dictionary<string, Guid> { ["bootstrap"] = Guid.NewGuid() }.ToImmutableDictionary()
        };
        fixture.Store.Profile = prepared;
        fixture.State.Publish(prepared);
        SetupAuthorityDouble authority = new();
        authority.Bootstrap = request =>
        {
            Assert.Contains(request.AttemptId, fixture.Store.Steps.Values.Select(step => step.AttemptId));
            Assert.True(fixture.Store.Commits >= 2);
            throw new HttpRequestException("sensitive response must not reach status");
        };
        SetupAuthorityCoordinator coordinator = new(fixture.Store, fixture.State, authority,
            new(new ProgressStore(), fixture.Secrets), fixture.Lease, fixture.Time,
            NullLogger<SetupAuthorityCoordinator>.Instance);

        // Act
        ServiceResult<SetupStatus> unknown = await coordinator.PrepareBootstrapAsync(CancellationToken.None);
        BootstrapPreparation original = authority.Last!;
        ServiceResult<SetupStatus> repeated = await coordinator.PrepareBootstrapAsync(CancellationToken.None);

        // Assert
        Assert.True(unknown.Success);
        Assert.True(repeated.Success);
        Assert.Equal("AuthorityResultUnknown", repeated.Data!.Reason);
        Assert.Equal(original, authority.Last);
        Assert.Single(fixture.Store.Steps);
        Assert.False(fixture.State.CommonOpen);
        Assert.NotNull(fixture.Store.Account!.PasswordHash);
    }

    [Fact]
    public async Task AcceptedReserveRemovesProtectedInvitationAndPreservesExactIds()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        Guid attemptId = Guid.NewGuid();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string reference = fixture.Secrets.Write(attemptId, "peer", Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
        EnrollmentRequest request = new(1, Guid.NewGuid(), 1, Guid.NewGuid(), attemptId, Guid.NewGuid(),
            Guid.NewGuid(), fixture.Options.PeerEndpoint, Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), new string('B', 64));
        ImmutableDictionary<string, Guid> operations = new Dictionary<string, Guid> { ["reserve"] = Guid.NewGuid() }.ToImmutableDictionary();
        string invitation = fixture.Secrets.ProtectInvitation(attemptId, "protected invitation"u8);
        LocalOnboardingAttempt intent = new(attemptId, request, EnrollmentRequestCodec.Digest(request), reference,
            1, operations, invitation, null);
        LocalInstallationProfile profile = fixture.Store.Profile! with
        {
            Mode = "Join", Phase = InstallationPhase.LocalPrepared, AttemptId = attemptId, NodeId = request.NodeId, ProfileRevision = 2,
            StorageGeneration = request.StorageGeneration, KeyReference = reference,
            PeerAddress = request.PeerUri, OperationIds = operations
        };
        fixture.Store.Profile = profile;
        fixture.State.Publish(profile);
        ProgressStore progress = new();
        SetupAuthorityDouble authority = new()
        {
            Join = saved =>
            {
                Assert.Equal(intent.Request, progress.Saved!.Request);
                if (!progress.Saved.ReservationIndex.HasValue)
                {
                    Assert.NotNull(progress.Saved.ProtectedInvitation);
                }
                Assert.Single(fixture.Store.Steps);
                return ServiceResult<SetupAuthorityEvidence>.Ok(new(attemptId, profile.RuntimeGeneration, 25,
                    saved.RequestDigest, SetupAuthorityEvidenceKind.ReservationAccepted));
            }
        };
        SetupAuthorityCoordinator coordinator = new(fixture.Store, fixture.State, authority,
            new(progress, fixture.Secrets), fixture.Lease, fixture.Time, NullLogger<SetupAuthorityCoordinator>.Instance);

        // Act
        ServiceResult<SetupStatus> reserved = await coordinator.PrepareJoinAsync(intent, CancellationToken.None);
        ServiceResult<SetupStatus> retry = await coordinator.PrepareJoinAsync(intent, CancellationToken.None);

        // Assert
        Assert.True(reserved.Success);
        Assert.True(retry.Success);
        Assert.Null(progress.Saved!.ProtectedInvitation);
        Assert.Equal(25, progress.Saved.ReservationIndex);
        Assert.Equal(intent.Request, progress.Saved.Request);
        Assert.Equal(operations, progress.Saved.OperationIds);
        Assert.Equal(reference, progress.Saved.PrivateKeyReference);
        Assert.False(fixture.State.CommonOpen);
    }

    [Fact]
    public void InvitationEnvelopeCannotCrossAttemptsAndCreatesNoOrphanFile()
    {
        // Arrange
        using InstallationFixture fixture = new();
        Guid attempt = Guid.NewGuid();

        // Act
        string protectedValue = fixture.Secrets.ProtectInvitation(attempt, "invitation"u8);
        byte[] plaintext = fixture.Secrets.ReadInvitation(attempt, protectedValue);

        // Assert
        Assert.Equal("invitation"u8.ToArray(), plaintext);
        Assert.DoesNotContain("invitation", protectedValue);
        Assert.Throws<InvalidDataException>(() => fixture.Secrets.ReadInvitation(Guid.NewGuid(), protectedValue));
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.KeyDirectory, "*.key"));
    }

    private class ProgressStore : IOnboardingProgressUnitOfWork, ILocalOnboardingAttemptRepository
    {
        public LocalOnboardingAttempt? Saved { get; private set; }
        public ILocalOnboardingAttemptRepository Attempts => this;
        public Task<LocalOnboardingAttempt?> FindAsync(Guid id, CancellationToken token)
        {
            return Task.FromResult(Saved);
        }

        public void Create(LocalOnboardingAttempt attempt)
        {
            Saved = attempt;
        }
        public Task ReplaceAsync(LocalOnboardingAttempt attempt, long revision, CancellationToken token)
        {
            Assert.Equal(revision, Saved!.Revision);
            Saved = attempt;
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
        }
    }
}
