namespace UptimeHarbor.Application.Cluster;

public interface IRevisionPayloadProtection
{
    string Protect(ReadOnlySpan<byte> payload);
    byte[] Unprotect(string payload);
}
