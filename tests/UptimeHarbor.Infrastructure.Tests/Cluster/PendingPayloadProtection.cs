using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class PendingPayloadProtection : IRevisionPayloadProtection
{
    public string Protect(ReadOnlySpan<byte> payload)
    {
        return Convert.ToBase64String(payload);
    }

    public byte[] Unprotect(string payload)
    {
        return Convert.FromBase64String(payload);
    }
}
