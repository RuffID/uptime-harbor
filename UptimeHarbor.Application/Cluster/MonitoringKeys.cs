using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringKeys
{
    public static Guid Id(string key)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    public static Guid Stream(Guid target, Guid observer, Guid storage, Guid generation)
    {
        return Id($"stream/{target:N}/{observer:N}/{storage:N}/{generation:N}");
    }

    public static string Digest<T>(T value)
    {
        return RevisionCodec.Digest(JsonSerializer.SerializeToUtf8Bytes(value));
    }
}
