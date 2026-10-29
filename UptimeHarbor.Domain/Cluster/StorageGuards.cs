using System.Text;

namespace UptimeHarbor.Domain.Cluster;

public static class StorageGuards
{
    public const int MAX_PAYLOAD_BYTES = 256 * 1024;

    public static void Id(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", name);
        }
    }

    public static string Text(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maxLength)
        {
            throw new ArgumentException("Value exceeds the storage limit.", name);
        }

        return value;
    }

    public static string Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 hexadecimal digest is required.", nameof(value));
        }

        return value.ToUpperInvariant();
    }

    public static string Payload(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > MAX_PAYLOAD_BYTES)
        {
            throw new ArgumentException("Payload exceeds the command limit.", nameof(value));
        }

        return value;
    }

    public static void Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC is required.", nameof(value));
        }
    }

    public static string PublicCertificate(string value)
    {
        Text(value, 16384, nameof(value));
        if (value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Private keys must remain local.", nameof(value));
        }

        return value;
    }
}
