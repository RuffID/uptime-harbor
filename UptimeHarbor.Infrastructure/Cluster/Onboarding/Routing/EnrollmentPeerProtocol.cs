namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Фиксированный whitelist peer RPC; не browser enrollment и не общий commands endpoint.</summary>
public static class EnrollmentPeerProtocol
{
    public const string PREFIX = "/internal/cluster/v1/enrollment";
    public const string COORDINATE = PREFIX + "/coordinate";
    public const string ISSUE = PREFIX + "/issue";
    public const string GRANT = PREFIX + "/grant";
    public const int MAX_BYTES = 65536;

    public static bool IsRoute(string? path)
    {
        return path is COORDINATE or ISSUE or GRANT;
    }
}
