using System.Net;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Секрет передаётся только тому browser HTTPS origin, который выбрал администратор.</summary>
public class EnrollmentEndpoints(SetupPeerAddressPolicy addresses)
{
    public const string PREFIX = "/internal/enrollment/v1/";

    public Uri Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
            || uri.AbsoluteUri != value || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".", StringComparison.Ordinal)
            || IPAddress.TryParse(uri.IdnHost, out IPAddress? ip) && !addresses.Allows(ip))
        {
            throw new InvalidDataException("EnrollmentOriginRejected");
        }

        return uri;
    }

    public Uri Resolve(Uri browserOrigin, EnrollmentEndpoint endpoint)
    {
        Origin(browserOrigin.AbsoluteUri);
        string action = endpoint switch
        {
            EnrollmentEndpoint.Discovery => "discovery",
            EnrollmentEndpoint.Nonce => "nonce",
            EnrollmentEndpoint.Reserve => "reserve",
            EnrollmentEndpoint.Issue => "issue",
            EnrollmentEndpoint.Admit => "admit",
            EnrollmentEndpoint.Resume => "resume",
            EnrollmentEndpoint.Renew => "renew",
            EnrollmentEndpoint.Probe => "probe",
            EnrollmentEndpoint.State => "state",
            _ => throw new InvalidDataException("EnrollmentEndpointUnsupported")
        };
        return new Uri(browserOrigin, PREFIX + action);
    }

    public void ValidateAdvertised(Uri browserOrigin, string enrollmentAddress, string peerAddress)
    {
        Uri origin = Origin(browserOrigin.AbsoluteUri);
        Uri enrollment = new(origin, PREFIX);
        if (enrollment.AbsoluteUri != enrollmentAddress)
        {
            throw new InvalidDataException("EnrollmentOriginChanged");
        }

        addresses.ValidateUri(peerAddress);
    }
}
