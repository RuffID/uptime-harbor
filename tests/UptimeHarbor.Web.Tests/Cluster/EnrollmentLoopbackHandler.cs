using System.Net;
using System.Net.Http.Headers;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Прямой вызов production gateway вместо сокетов/TLS. Настоящий transport сохраняет codec/route/bounds.</summary>
public class EnrollmentLoopbackHandler(ManagedBootstrapFixture first) : HttpMessageHandler
{
    public bool LoseReserveResponse { get; set; }
    public Action? BeforeIssue { get; set; }
    public List<string> Origins { get; } = [];
    public UptimeHarbor.Infrastructure.Cluster.Onboarding.EnrollmentPeerProbes? Probes { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Origins.Add(request.RequestUri!.GetLeftPart(UriPartial.Authority));
        string action = request.RequestUri.AbsolutePath.Split('/')[^1];
        byte[] body = [];
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(token);
        }

        object result;
        if (action == "discovery")
        {
            result = first.Descriptors.Discover();
        }
        else if (action == "nonce")
        {
            EnrollmentNonceRequest nonce = StrictOnboardingJson.Decode<EnrollmentNonceRequest>(body);
            result = first.Nonces.Issue(nonce.AttemptId, nonce.BootId, nonce.BootChallenge, nonce.ProcessEpoch, nonce.Action, nonce.RequestDigest);
        }
        else
        {
            EnrollmentExchange exchange = StrictOnboardingJson.Decode<EnrollmentExchange>(body);
            if (action == "state")
            {
                result = await first.Enrollment.StateAsync(exchange, token);
            }
            else if (action == "probe")
            {
                EnrollmentDescriptor descriptor = await first.Enrollment.ConfirmProbeAsync(exchange, token);
                await Probes!.ReverseAsync(descriptor, token);
                result = descriptor;
            }
            else
            {
                if (action == "issue")
                {
                    BeforeIssue?.Invoke();
                }

                result = await first.Enrollment.ExchangeAsync(exchange, token);
                if (action == "reserve" && LoseReserveResponse)
                {
                    LoseReserveResponse = false;
                    throw new HttpRequestException("TestLostReserveResponseAfterAccepted");
                }
            }
        }

        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(StrictOnboardingJson.Encode(result)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }
}
