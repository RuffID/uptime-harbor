using System.Buffers;
using System.Text.Json;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Потоковая проверка duplicate fields до десериализации принятого образа.</summary>
public static class StrictSnapshotJson
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        try
        {
            JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 32 });
            Stack<HashSet<string>> fields = new();
            int consumed = Consume(bytes, true, ref state, fields);
            if (consumed != bytes.Length || fields.Count != 0)
            {
                throw new InvalidDataException("SnapshotJsonInvalid");
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("SnapshotJsonInvalid");
        }
    }

    public static async Task ValidateAsync(Stream source, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(2097152);
        try
        {
            JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 32 });
            Stack<HashSet<string>> fields = new();
            int retained = 0;
            bool final = false;
            while (!final)
            {
                int read = await source.ReadAsync(buffer.AsMemory(retained), token);
                final = read == 0;
                int length = retained + read;
                int consumed = Consume(buffer.AsSpan(0, length), final, ref state, fields);
                retained = length - consumed;
                buffer.AsSpan(consumed, retained).CopyTo(buffer);
                if (retained == buffer.Length)
                {
                    throw new InvalidDataException("SnapshotJsonTokenLimit");
                }
            }

            if (retained != 0 || fields.Count != 0)
            {
                throw new InvalidDataException("SnapshotJsonInvalid");
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("SnapshotJsonInvalid");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static int Consume(ReadOnlySpan<byte> bytes, bool final, ref JsonReaderState state, Stack<HashSet<string>> fields)
    {
        Utf8JsonReader reader = new(bytes, final, state);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                fields.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                fields.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && !fields.Peek().Add(reader.GetString()!))
            {
                throw new InvalidDataException("SnapshotDuplicateField");
            }
        }

        state = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }
}
