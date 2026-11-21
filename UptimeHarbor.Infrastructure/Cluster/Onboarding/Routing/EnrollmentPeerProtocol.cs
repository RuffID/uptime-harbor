namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Фиксированный whitelist peer RPC; не browser enrollment и не общий commands endpoint.</summary>
public static class EnrollmentPeerProtocol
{
    public const string PREFIX = "/internal/cluster/v1/enrollment";
    public const string COORDINATE = PREFIX + "/coordinate";
    public const string ISSUE = PREFIX + "/issue";
    public const string GRANT = PREFIX + "/grant";
    public const string RENEW = PREFIX + "/renew";
    public const string HANDOFF = PREFIX + "/handoff";
    public const string RETIREMENT_HANDOFF = PREFIX + "/retirement-handoff";
    public const string ROTATION_NONCE = PREFIX + "/rotation/nonce";
    public const string ROTATION_AUTHORIZE = PREFIX + "/rotation/authorize";
    public const string ROTATION_BIND = PREFIX + "/rotation/bind";
    public const string ROTATION_RETIRE = PREFIX + "/rotation/retire";
    public const int MAX_BYTES = 65536;

    public static bool IsRoute(string? path)
    {
        return path is COORDINATE or ISSUE or GRANT or RENEW or HANDOFF or RETIREMENT_HANDOFF || IsRotationAdmission(path);
    }

    public static bool IsRotationAdmission(string? path)
    {
        return path is ROTATION_NONCE or ROTATION_AUTHORIZE or ROTATION_BIND or ROTATION_RETIRE;
    }
}
