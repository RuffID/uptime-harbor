using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public static class RevisionBatchCodec
{
    public static IReadOnlyList<RevisionCommand> Decode(ReadOnlyMemory<byte> bytes, ClusterOptions options)
    {
        if (bytes.Length == 0 || bytes.Length > options.BatchMaxBytes)
        {
            throw new InvalidDataException("BatchLimitExceeded");
        }

        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0
            || document.RootElement.GetArrayLength() > options.BatchMaxEntries)
        {
            throw new InvalidDataException("InvalidBatchCount");
        }

        List<RevisionCommand> commands = [];
        HashSet<Guid> ids = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(element.GetRawText());
            if (payload.Length > options.CommandMaxBytes)
            {
                throw new InvalidDataException("CommandLimitExceeded");
            }

            RevisionCommand command = RevisionCodec.Decode(payload);
            if (!ids.Add(command.OperationId))
            {
                throw new InvalidDataException("DuplicateBatchOperation");
            }

            commands.Add(command);
        }

        return commands;
    }

    public static async Task<byte[]> ReadAsync(Stream source, int limit, CancellationToken token)
    {
        await using LimitedMemoryStream destination = new(limit);
        await source.CopyToAsync(destination, 16384, token);
        return destination.ToArray();
    }
}
