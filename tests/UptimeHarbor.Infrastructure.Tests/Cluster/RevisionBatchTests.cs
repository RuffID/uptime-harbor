using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class RevisionBatchTests
{
    [Fact]
    public void WholeBatchValidationRejectsDuplicatesUnsupportedTailAndLimits()
    {
        // Arrange
        RevisionFixture fixture = new();
        RevisionCommand first = fixture.Server();
        RevisionCommand second = fixture.Server();
        ClusterOptions options = fixture.Options("unused");

        // Act
        var duplicate = Record.Exception(() => RevisionBatchCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(new[] { first, first }), options));
        var malformed = Record.Exception(() => RevisionBatchCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(new[] { first, second with { Schema = 99 } }), options));
        options.BatchMaxEntries = 1;
        var count = Record.Exception(() => RevisionBatchCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(new[] { first, second }), options));
        options.CommandMaxBytes = 10;
        var size = Record.Exception(() => RevisionBatchCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(new[] { first }), options));

        // Assert
        Assert.IsType<InvalidDataException>(duplicate);
        Assert.IsType<InvalidDataException>(malformed);
        Assert.IsType<InvalidDataException>(count);
        Assert.IsType<InvalidDataException>(size);
    }

    [Fact]
    public void ResultReorderingIsMatchedByOperationId_WhileGapDuplicateOrCorruptionRejectsWholeResponse()
    {
        // Arrange
        RevisionFixture fixture = new();
        RevisionCommand first = fixture.Server();
        RevisionCommand second = fixture.Server();
        CommandResult a = new(first.OperationId, RevisionCodec.Digest(first), OperationOutcome.Committed, 3, 1, 3);
        CommandResult b = new(second.OperationId, RevisionCodec.Digest(second), OperationOutcome.Conflict, 4, 2);
        RevisionCommand[] batch = [first, second];

        // Act
        PendingReplicationPump.ValidateResults(batch, [b, a]);
        var gap = Record.Exception(() => PendingReplicationPump.ValidateResults(batch, [a]));
        var duplicate = Record.Exception(() => PendingReplicationPump.ValidateResults(batch, [a, a]));
        var corrupt = Record.Exception(() => PendingReplicationPump.ValidateResults(batch, [a, b with { Digest = new string('B', 64) }]));

        // Assert
        Assert.IsType<InvalidDataException>(gap);
        Assert.IsType<InvalidDataException>(duplicate);
        Assert.IsType<InvalidDataException>(corrupt);
    }

    [Fact]
    public async Task ActualStreamLimitDoesNotRelyOnContentLength()
    {
        // Arrange
        using MemoryStream stream = new(new byte[11]);

        // Act
        Exception? error = await Record.ExceptionAsync(() => RevisionBatchCodec.ReadAsync(stream, 10, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }
}
