namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Ограничивает фактически записанные байты, включая native snapshot без Content-Length.</summary>
public class LimitedMemoryStream(int maximumBytes) : MemoryStream
{
    public override void SetLength(long value)
    {
        if (value > maximumBytes)
        {
            throw new InvalidDataException("PayloadLimitExceeded");
        }

        base.SetLength(value);
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacityFor(1);
        base.WriteByte(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacityFor(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacityFor(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnsureCapacityFor(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureCapacityFor(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void EnsureCapacityFor(int count)
    {
        if (Position + count > maximumBytes)
        {
            throw new InvalidDataException("PayloadLimitExceeded");
        }
    }
}
