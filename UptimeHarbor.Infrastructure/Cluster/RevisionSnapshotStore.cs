using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Потоковые immutable snapshot; публикация следует за commit checkpoint проекции.</summary>
public class RevisionSnapshotStore(IOptions<ClusterOptions> configured)
{
    public const int MAX_SNAPSHOT_BYTES = 67108864;
    private const int MAX_MANIFEST_BYTES = 1048576;
    private static readonly byte[] MAGIC = "UHRVS001"u8.ToArray();
    private static readonly JsonSerializerOptions JSON = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private PublishedRevisionSnapshot? published;
    public PublishedRevisionSnapshot? Current => Volatile.Read(ref published);

    public async Task<PublishedRevisionSnapshot> PublishAsync(RevisionImage image, CancellationToken token)
    {
        OnboardingProtocol.ValidateSnapshot(image, configured.Value);
        OnboardingSnapshot.Validate(image);
        string directory = SnapshotDirectory();
        Guid id = Guid.NewGuid();
        string bodyPath = Path.Combine(directory, id.ToString("N") + ".body.tmp");
        string temporary = Path.Combine(directory, id.ToString("N") + ".snapshot.tmp");
        try
        {
            long reserve = Math.Min(MAX_SNAPSHOT_BYTES, configured.Value.NativeMaxBytes);
            if (new DriveInfo(Path.GetPathRoot(temporary)!).AvailableFreeSpace < reserve * 2)
            {
                throw new IOException("SnapshotDiskCapacityUnavailable");
            }

            await using (FileStream body = OpenWrite(bodyPath))
            {
                await using BoundedWriteStream bounded = new(body, reserve);
                await JsonSerializer.SerializeAsync(bounded, image, JSON, token);
                ValidateLength(body.Length);
                await FlushAsync(body, token);
            }

            await using FileStream source = OpenRead(bodyPath);
            int chunkBytes = configured.Value.SnapshotChunkMaxBytes;
            List<string> chunks = await ReadChunksAsync(source, chunkBytes, null, token);
            source.Position = 0;
            string bodyDigest = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
            RevisionSnapshotManifest manifest = new(id, image.ClusterId, image.Generation, image.Index, image.Term,
                configured.Value.Compatibility, source.Length, bodyDigest, chunkBytes, chunks);
            byte[] header = JsonSerializer.SerializeToUtf8Bytes(manifest, JSON);
            if (header.Length > MAX_MANIFEST_BYTES)
            {
                throw new InvalidDataException("SnapshotManifestLimitExceeded");
            }

            await using (FileStream destination = OpenWrite(temporary))
            {
                byte[] length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
                await destination.WriteAsync(MAGIC, token);
                await destination.WriteAsync(length, token);
                await destination.WriteAsync(header, token);
                source.Position = 0;
                await source.CopyToAsync(destination, token);
                ValidateLength(destination.Length);
                await FlushAsync(destination, token);
            }

            string path = SnapshotPath(id);
            PublishedRevisionSnapshot verified = await ReadAsync(temporary, null, token);
            UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
            File.Move(temporary, path);
            return verified with { Path = path };
        }
        finally
        {
            File.Delete(bodyPath);
            File.Delete(temporary);
        }
    }

    public async Task<PublishedRevisionSnapshot> LoadAsync(Guid snapshotId, string expectedDigest, CancellationToken token)
    {
        PublishedRevisionSnapshot snapshot = await ReadAsync(SnapshotPath(snapshotId), expectedDigest, token);
        if (snapshot.Manifest.SnapshotId != snapshotId)
        {
            throw new InvalidDataException("SnapshotIdentityMismatch");
        }

        UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
        Volatile.Write(ref published, snapshot);
        return snapshot;
    }

    public async Task<PublishedRevisionSnapshot> ReadAsync(string path, string? expectedDigest, CancellationToken token)
    {
        await using FileStream stream = OpenRead(path);
        return await DecodeAsync(stream, expectedDigest, path, token);
    }

    public async Task<PublishedRevisionSnapshot> DecodeAsync(Stream stream, string? expectedDigest, string path, CancellationToken token)
    {
        ValidateLength(stream.Length);
        stream.Position = 0;
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (expectedDigest is not null && digest != expectedDigest)
        {
            throw new InvalidDataException("SnapshotHashMismatch");
        }

        stream.Position = 0;
        byte[] prefix = new byte[12];
        await stream.ReadExactlyAsync(prefix, token);
        int headerBytes = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (!prefix.AsSpan(0, 8).SequenceEqual(MAGIC) || headerBytes is <= 0 or > MAX_MANIFEST_BYTES)
        {
            throw new InvalidDataException("SnapshotHeaderInvalid");
        }

        byte[] header = new byte[headerBytes];
        await stream.ReadExactlyAsync(header, token);
        RevisionSnapshotManifest manifest = DecodeManifest(header);
        if (manifest.ClusterId != configured.Value.ClusterId || manifest.Generation != configured.Value.Generation
            || manifest.Compatibility != configured.Value.Compatibility || manifest.Index <= 0 || manifest.Term <= 0
            || manifest.SnapshotId == Guid.Empty || manifest.ChunkBytes is < 16384 or > 1048576
            || manifest.ChunkDigests is null || manifest.BodyBytes != stream.Length - stream.Position)
        {
            throw new InvalidDataException("SnapshotIncompatible");
        }

        long bodyOffset = stream.Position;
        string bodyDigest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (bodyDigest != manifest.BodyDigest)
        {
            throw new InvalidDataException("SnapshotBodyCorrupt");
        }

        stream.Position = bodyOffset;
        await ReadChunksAsync(stream, manifest.ChunkBytes, manifest.ChunkDigests, token);
        stream.Position = bodyOffset;
        await StrictSnapshotJson.ValidateAsync(stream, token);
        stream.Position = bodyOffset;
        RevisionImage image;
        try
        {
            image = await JsonSerializer.DeserializeAsync<RevisionImage>(stream, JSON, token)
                ?? throw new InvalidDataException("SnapshotImageMissing");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("SnapshotImageInvalid");
        }
        if (image.ClusterId != manifest.ClusterId || image.Generation != manifest.Generation
            || image.Index != manifest.Index || image.Term != manifest.Term || image.Digest is null)
        {
            throw new InvalidDataException("SnapshotCutMismatch");
        }

        image = SnapshotMonitoringCompatibility.Upgrade(image);
        OnboardingProtocol.ValidateSnapshot(image, configured.Value);
        OnboardingSnapshot.Validate(image);
        return new PublishedRevisionSnapshot(manifest, digest, path, image);
    }

    public async Task<PublishedRevisionSnapshot> ImportAsync(Func<Stream, CancellationToken, ValueTask> copy, CancellationToken token)
    {
        string temporary = Path.Combine(SnapshotDirectory(), Guid.NewGuid().ToString("N") + ".import.tmp");
        try
        {
            long reserve = Math.Min(MAX_SNAPSHOT_BYTES, configured.Value.NativeMaxBytes);
            if (new DriveInfo(Path.GetPathRoot(temporary)!).AvailableFreeSpace < reserve)
            {
                throw new IOException("SnapshotDiskCapacityUnavailable");
            }

            await using (FileStream destination = OpenWrite(temporary))
            {
                await using BoundedWriteStream bounded = new(destination, reserve);
                await copy(bounded, token);
                ValidateLength(destination.Length);
                await FlushAsync(destination, token);
            }

            PublishedRevisionSnapshot verified = await ReadAsync(temporary, null, token);
            string path = SnapshotPath(verified.Manifest.SnapshotId);
            if (File.Exists(path))
            {
                return await ReadAsync(path, verified.Digest, token);
            }

            UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
            File.Move(temporary, path);
            return verified with { Path = path };
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void ConfirmInstalled(PublishedRevisionSnapshot snapshot)
    {
        UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
        Volatile.Write(ref published, snapshot);
    }

    public void Reclaim(long watermark)
    {
        PublishedRevisionSnapshot? current = Current;
        if (current is null || watermark < current.Manifest.Index)
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(current.Path)!, "*.snapshot"))
        {
            if (path == current.Path)
            {
                continue;
            }

            using FileStream stream = OpenRead(path);
            ValidateLength(stream.Length);
            byte[] prefix = new byte[12];
            stream.ReadExactly(prefix);
            int headerBytes = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
            if (!prefix.AsSpan(0, 8).SequenceEqual(MAGIC) || headerBytes is <= 0 or > MAX_MANIFEST_BYTES)
            {
                throw new InvalidDataException("SnapshotHeaderInvalid");
            }

            byte[] header = new byte[headerBytes];
            stream.ReadExactly(header);
            RevisionSnapshotManifest old = DecodeManifest(header);
            if (old.ClusterId == current.Manifest.ClusterId && old.Generation == current.Manifest.Generation
                && old.Index < current.Manifest.Index && old.Index < watermark)
            {
                File.Delete(path);
            }
        }
    }

    private static RevisionSnapshotManifest DecodeManifest(byte[] header)
    {
        StrictSnapshotJson.Validate(header);
        try
        {
            return JsonSerializer.Deserialize<RevisionSnapshotManifest>(header, JSON)
                ?? throw new InvalidDataException("SnapshotManifestMissing");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("SnapshotManifestInvalid");
        }
    }

    private static async Task<List<string>> ReadChunksAsync(Stream stream, int chunkBytes, IReadOnlyList<string>? expected, CancellationToken token)
    {
        if (chunkBytes is < 16384 or > 1048576)
        {
            throw new InvalidDataException("SnapshotChunkLimitInvalid");
        }

        byte[] buffer = new byte[chunkBytes];
        List<string> chunks = [];
        while (stream.Position < stream.Length)
        {
            int count = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            string digest = RevisionCodec.Digest(buffer.AsSpan(0, count));
            if (expected is not null && (chunks.Count >= expected.Count || expected[chunks.Count] != digest))
            {
                throw new InvalidDataException("SnapshotChunkCorrupt");
            }

            chunks.Add(digest);
        }

        if (expected is not null && chunks.Count != expected.Count)
        {
            throw new InvalidDataException("SnapshotChunkCountMismatch");
        }

        return chunks;
    }

    private string SnapshotDirectory()
    {
        string directory = Path.Combine(configured.Value.StoragePath, "snapshots");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string SnapshotPath(Guid id)
    {
        return Path.Combine(configured.Value.StoragePath, "snapshots", id.ToString("N") + ".snapshot");
    }

    private void ValidateLength(long length)
    {
        long limit = Math.Min(MAX_SNAPSHOT_BYTES, configured.Value.NativeMaxBytes - 65536);
        if (length <= 0 || length > limit)
        {
            throw new InvalidDataException("SnapshotLengthInvalid");
        }
    }

    private static FileStream OpenRead(string path)
    {
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
    }

    private static FileStream OpenWrite(string path)
    {
        return new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    private static async Task FlushAsync(FileStream stream, CancellationToken token)
    {
        await stream.FlushAsync(token);
        stream.Flush(flushToDisk: true);
    }
}
