namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Ограничивает поток snapshot, не владея нижележащим ресурсом.</summary>
public sealed class BoundedWriteStream(Stream destination, long maximumBytes) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => destination.Length;
    public override long Position { get => destination.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureLimit(count);
        destination.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureLimit(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
        destination.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return destination.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    private void EnsureLimit(int count)
    {
        if (destination.Position + count > maximumBytes)
        {
            throw new InvalidDataException("SnapshotLimitExceeded");
        }
    }
}
