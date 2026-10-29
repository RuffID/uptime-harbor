using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class RevisionSnapshotTests
{
    [Fact]
    public async Task PublishRequiresCheckpointConfirmation_AndReceiverUsesManifestChunkSize()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore store = new(Options.Create(options));

        // Act
        PublishedRevisionSnapshot snapshot = await store.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        options.SnapshotChunkMaxBytes = 32768;
        PublishedRevisionSnapshot verified = await store.LoadAsync(snapshot.Manifest.SnapshotId, snapshot.Digest,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(16384, verified.Manifest.ChunkBytes);
        Assert.Equal(snapshot.Digest, verified.Digest);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Location, "snapshots"), "*.tmp"));
        Assert.Equal(fixture.Baseline.Values[fixture.Actor], verified.Image.Values[fixture.Actor]);
        Assert.Equal(snapshot.Manifest.SnapshotId, store.Current!.Manifest.SnapshotId);
    }

    [Fact]
    public async Task StreamingImportRejectsCorruptionAndOverflowAndRemovesTemporaryFiles()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore store = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await store.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        store.ConfirmInstalled(snapshot);
        byte[] bytes = await File.ReadAllBytesAsync(snapshot.Path, TestContext.Current.CancellationToken);

        // Act
        PublishedRevisionSnapshot imported = await store.ImportAsync((stream, token) => stream.WriteAsync(bytes, token),
            TestContext.Current.CancellationToken);
        bytes[^1] ^= 1;
        Exception? corrupt = await Record.ExceptionAsync(() => store.ImportAsync((stream, token) => stream.WriteAsync(bytes, token),
            TestContext.Current.CancellationToken));
        options.NativeMaxBytes = 16;
        Exception? overflow = await Record.ExceptionAsync(() => store.ImportAsync((stream, token) => stream.WriteAsync(bytes, token),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(snapshot.Digest, imported.Digest);
        Assert.IsType<InvalidDataException>(corrupt);
        Assert.IsType<InvalidDataException>(overflow);
        Assert.Same(snapshot, store.Current);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Location, "snapshots"), "*.tmp"));
    }

    [Fact]
    public async Task CorruptOrIncompatibleSnapshotCannotReplacePublishedCut()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore store = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await store.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        Assert.Null(store.Current);
        store.ConfirmInstalled(snapshot);
        byte[] bytes = await File.ReadAllBytesAsync(snapshot.Path, TestContext.Current.CancellationToken);
        bytes[^1] ^= 1;

        // Act
        using MemoryStream stream = new(bytes);
        Exception? corrupt = await Record.ExceptionAsync(() => store.DecodeAsync(stream, snapshot.Digest, snapshot.Path, TestContext.Current.CancellationToken));
        options.Generation = 2;
        Exception? incompatible = await Record.ExceptionAsync(() => store.LoadAsync(snapshot.Manifest.SnapshotId,
            snapshot.Digest, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(corrupt);
        Assert.IsType<InvalidDataException>(incompatible);
        Assert.Same(snapshot, store.Current);
    }

    [Fact]
    public async Task CompactionRetainsDecisionsControlsAndTombstones_AndReaderWatermarkProtectsSnapshot()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        RevisionSnapshotStore store = new(Options.Create(fixture.Options(directory.Location)));
        RevisionReducer reducer = new();
        RevisionCommand command = fixture.Server();
        RevisionCommand cancel = command with { OperationId = Guid.NewGuid(), Action = "Cancel", Value = null, Target = command };
        RevisionImage cancelled = reducer.Apply(fixture.Baseline, cancel, 3, 1);
        PublishedRevisionSnapshot old = await store.PublishAsync(cancelled, TestContext.Current.CancellationToken);
        RevisionCommand other = fixture.Server();
        RevisionImage accepted = reducer.Apply(cancelled, other, 4, 1);
        RevisionCommand delete = other with { OperationId = Guid.NewGuid(), Action = "Delete", Value = null, BaseRevision = 4 };
        RevisionImage deleted = reducer.Apply(accepted, delete, 5, 2);
        PublishedRevisionSnapshot latest = await store.PublishAsync(deleted, TestContext.Current.CancellationToken);
        store.ConfirmInstalled(latest);

        // Act
        store.Reclaim(4);
        bool retainedBelowCut = File.Exists(old.Path);
        store.Reclaim(5);
        PublishedRevisionSnapshot replay = await store.LoadAsync(latest.Manifest.SnapshotId, latest.Digest, TestContext.Current.CancellationToken);
        RevisionImage repeated = reducer.Apply(replay.Image, command, 6, 2);

        // Assert
        Assert.True(retainedBelowCut);
        Assert.False(File.Exists(old.Path));
        Assert.True(File.Exists(latest.Path));
        Assert.True(repeated.Values[other.EntityId].Deleted);
        Assert.Equal(OperationOutcome.Cancelled, repeated.Results[command.OperationId].Outcome);
        Assert.Equal(cancelled.Results[command.OperationId], repeated.Results[command.OperationId]);
        Assert.Single(repeated.Controls);
    }

    [Fact]
    public void SnapshotSchemaExcludesLocalStateAndRejectsStaleCredentialRows()
    {
        // Arrange
        RevisionFixture fixture = new();
        using UptimeHarborDbContext context = RevisionFixture.CreateContext();
        RevisionImage image = fixture.WithProjectionRows();
        SharedSnapshotRow user = Assert.Single(image.ProjectionRows, row => row.Entity == typeof(ApplicationUser).FullName);
        RevisionImage corrupt = image with
        {
            ProjectionRows = image.ProjectionRows.Replace(user, user with
            {
                Fields = user.Fields.SetItem("PasswordHash", JsonSerializer.SerializeToElement("stale-hash"))
            })
        };

        // Act
        SnapshotImageValidation.Validate(context, image);
        Exception? rejected = Record.Exception(() => SnapshotImageValidation.Validate(context, corrupt));

        // Assert
        Assert.IsType<InvalidDataException>(rejected);
        Assert.DoesNotContain("AccessFailedCount", user.Fields.Keys);
        Assert.DoesNotContain("LockoutEnd", user.Fields.Keys);
        Assert.DoesNotContain(SnapshotSchema.Entities(context), type => type.ClrType == typeof(LocalNodeState)
            || type.ClrType == typeof(PendingOperation) || type.ClrType.Name.Contains("DataProtection", StringComparison.Ordinal));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void StreamLimitsCoverByteWritesAndSetLength()
    {
        // Arrange
        using LimitedMemoryStream stream = new(2);
        stream.Write([1, 2]);

        // Act
        Exception? write = Record.Exception(() => stream.WriteByte(3));
        Exception? resize = Record.Exception(() => stream.SetLength(3));

        // Assert
        Assert.IsType<InvalidDataException>(write);
        Assert.IsType<InvalidDataException>(resize);
        Assert.Equal(2, stream.Length);
    }
}
