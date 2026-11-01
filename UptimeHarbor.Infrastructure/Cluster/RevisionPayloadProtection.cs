using Microsoft.AspNetCore.DataProtection;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class RevisionPayloadProtection(IDataProtectionProvider provider) : IRevisionPayloadProtection
{
    private readonly IDataProtector protector = provider.CreateProtector("UptimeHarbor.Cluster.Pending.v1");

    public string Protect(ReadOnlySpan<byte> payload)
    {
        return Convert.ToBase64String(protector.Protect(payload.ToArray()));
    }

    public byte[] Unprotect(string payload)
    {
        return protector.Unprotect(Convert.FromBase64String(payload));
    }
}
