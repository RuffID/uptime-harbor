using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class AccountBootstrapTests
{
    [Theory]
    [InlineData("Join")]
    [InlineData("Restart")]
    [InlineData("Disabled")]
    public async Task JoinAndRestartCannotPrepareAnotherFirstAdministrator(string mode)
    {
        // Arrange
        using AcceptedIdentityFixture identity = new();
        ClusterOptions options = Options(mode);
        Writer writer = new(identity.State);
        Admission admission = new();
        ClusterAccountBootstrapper bootstrap = new(Microsoft.Extensions.Options.Options.Create(options),
            identity.Credentials, admission, identity.State, writer, new Audit());

        // Act
        ServiceResult<RevisionCommand> prepared = await bootstrap.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, "admin", "Secret-123!", TestContext.Current.CancellationToken);
        bool initialized = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(prepared.Success);
        Assert.False(initialized);
        Assert.Equal(0, writer.Calls);
        Assert.Null(identity.State.Current);
    }

    [Fact]
    public async Task ExactProvenBootstrapCreatesOneAccountAndStableRoleThroughProductionReducer()
    {
        // Arrange
        using AcceptedIdentityFixture identity = new();
        ClusterOptions options = Options("Bootstrap");
        Writer writer = new(identity.State);
        Admission admission = new();
        Audit audit = new();
        ClusterAccountBootstrapper bootstrap = new(Microsoft.Extensions.Options.Options.Create(options),
            identity.Credentials, admission, identity.State, writer, audit);
        RevisionCommand command = (await bootstrap.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, "first", "Secret-123!", TestContext.Current.CancellationToken)).Data!;
        identity.State.Publish(new RevisionImage(options.ClusterId, 1));

        // Act
        bool withoutProof = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);
        admission.Proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration, true, false, null, null, 0, 0)
        {
            BootstrapAccount = command
        };
        identity.State.Publish(identity.State.Current! with { BootstrapAccountDigest = RevisionCodec.Digest(command) });
        bool first = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);
        bool repeated = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);
        RevisionImage replayed = new RevisionReducer().Apply(identity.State.Current!, command, 2, 2);

        // Assert
        Assert.False(withoutProof);
        Assert.True(first);
        Assert.False(repeated);
        Assert.Equal(1, writer.Calls);
        Assert.Single(audit.Outcomes);
        AcceptedValue account = Assert.Single(replayed.Values.Values);
        Assert.Equal(command.EntityId, account.Id);
        Assert.Equal(command.Value!.RoleId, account.Value.RoleId);
        Assert.Equal(1, account.Revision);
        Assert.Single(replayed.Results);
        Assert.True(identity.State.Find(account.Id)!.IsEnabled);
        Assert.DoesNotContain("Secret-123!", System.Text.Encoding.UTF8.GetString(RevisionCodec.Encode(command)));

        // Act
        ServiceResult<RevisionCommand> duplicate = await bootstrap.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, "extra", "Secret-123!", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(duplicate.Success);
    }

    [Fact]
    public async Task WrongDigestAndOldBackupCannotBootstrapOrReactivateAccount()
    {
        // Arrange
        using AcceptedIdentityFixture identity = new();
        ClusterOptions options = Options("Bootstrap");
        Writer writer = new(identity.State);
        Admission admission = new();
        ClusterAccountBootstrapper bootstrap = new(Microsoft.Extensions.Options.Options.Create(options),
            identity.Credentials, admission, identity.State, writer, new Audit());
        RevisionCommand command = (await bootstrap.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, "first", "Secret-123!", TestContext.Current.CancellationToken)).Data!;
        identity.State.Publish(new RevisionImage(options.ClusterId, 1) { BootstrapAccountDigest = new string('A', 64) });
        admission.Proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration, true, false, null, null, 0, 0)
        {
            BootstrapAccount = command
        };

        // Act
        bool wrongDigest = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);
        RevisionImage rejected = new RevisionReducer().Apply(identity.State.Current!, command, 1, 1);
        admission.Proof = admission.Proof with { OldBackup = true };
        identity.State.Publish(identity.State.Current! with { BootstrapAccountDigest = RevisionCodec.Digest(command) });
        bool restoredBackup = await bootstrap.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(wrongDigest);
        Assert.False(restoredBackup);
        Assert.Equal(0, writer.Calls);
        Assert.Empty(rejected.Values);
        Assert.Equal(OperationOutcome.Rejected, rejected.Results[command.OperationId].Outcome);
    }

    private static ClusterOptions Options(string mode)
    {
        return new ClusterOptions { Mode = mode, ClusterId = Guid.NewGuid(), NodeId = Guid.NewGuid(),
            StorageGeneration = Guid.NewGuid(), Generation = 1 };
    }

    private class Admission : IRecoveryAdmission
    {
        public RecoveryAdmission? Proof { get; set; }
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token)
        {
            return ValueTask.FromResult(Proof);
        }
    }

    private class Writer(RevisionStateStore state) : IConsensusWriter
    {
        public int Calls { get; private set; }
        public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken cancellationToken)
        {
            Calls++;
            RevisionImage image = state.Current!;
            state.Publish(new RevisionReducer().Apply(image, RevisionCodec.Decode(payload.Span), image.Index + 1, 1));
            return ValueTask.FromResult(state.Current!.Index);
        }

        public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private class Audit : IRevisionAudit
    {
        public List<string> Outcomes { get; } = [];
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
            Outcomes.Add(outcome);
        }
    }
}
