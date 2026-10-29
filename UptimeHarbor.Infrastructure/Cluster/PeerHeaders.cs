using System.Globalization;
using System.Text.Json;

namespace UptimeHarbor.Infrastructure.Cluster;

public static class PeerHeaders
{
    public const string ENVELOPE = "X-UptimeHarbor-Peer";

    public static string Encode(PeerEnvelope envelope)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    public static PeerEnvelope? Decode(string value)
    {
        if (value.Length > 4096)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PeerEnvelope>(Convert.FromBase64String(value));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}
