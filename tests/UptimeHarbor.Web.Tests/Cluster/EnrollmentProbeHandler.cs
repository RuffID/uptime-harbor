using System.Net;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Без sockets: обе стороны вызывают production TLS policy и peer probe validator.</summary>
public class EnrollmentProbeHandler(Func<EnrollmentPeerProbe, EnrollmentPeerProbeReply> reply) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath != EnrollmentPeerProbeTransport.PATH || request.Method != HttpMethod.Post)
        {
            throw new InvalidDataException("TestUnexpectedPeerProbeRoute");
        }

        Calls++;
        EnrollmentPeerProbe probe = StrictOnboardingJson.Decode<EnrollmentPeerProbe>(await request.Content!.ReadAsByteArrayAsync(token));
        EnrollmentPeerProbeReply result = reply(probe);
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(StrictOnboardingJson.Encode(result)) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
}
