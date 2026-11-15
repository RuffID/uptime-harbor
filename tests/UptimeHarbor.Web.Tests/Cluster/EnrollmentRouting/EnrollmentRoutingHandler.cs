using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Реальные mTLS policy/middleware/controller без TLS handshake, listeners и MVC host.</summary>
public class EnrollmentRoutingHandler(EnrollmentRoutingFixture fixture, EnrollmentRoutingNode source) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath;
        EnrollmentRoutingNode target = fixture.Nodes.Single(node => new Uri(node.Options.PublicUri).Authority == request.RequestUri.Authority);
        byte[] body = await request.Content!.ReadAsByteArrayAsync(token);
        fixture.Sent.Add((source.Options.NodeId, target.Options.NodeId, path, body));
        fixture.BeforeSend?.Invoke(target, path);
        if (fixture.OfflineSigner && path == EnrollmentPeerProtocol.ISSUE)
        {
            throw new HttpRequestException("TestSignerOffline");
        }

        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new(request.RequestUri.Authority);
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Request.Headers[PeerHeaders.ENVELOPE] = request.Headers.GetValues(PeerHeaders.ENVELOPE).Single();
        context.Connection.LocalPort = target.Options.PeerListenPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
        context.Connection.ClientCertificate = source.Certificate;
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit());
        context.Response.Body = new MemoryStream();
        context.RequestAborted = token;
        PeerBoundaryMiddleware boundary = new(async current =>
        {
            EnrollmentPeerController controller = new(target.Service, target.Limits, target.Log)
            {
                ControllerContext = new() { HttpContext = current }
            };
            IActionResult result;
            if (path == EnrollmentPeerProtocol.COORDINATE)
            {
                result = await controller.CoordinateAsync(token);
            }
            else if (path == EnrollmentPeerProtocol.ISSUE)
            {
                result = await controller.IssueAsync(token);
            }
            else
            {
                result = await controller.GrantAsync(token);
            }

            if (result is FileContentResult file)
            {
                current.Response.ContentType = file.ContentType;
                await current.Response.Body.WriteAsync(file.FileContents, token);
            }
            else if (result is StatusCodeResult status)
            {
                current.Response.StatusCode = status.StatusCode;
            }
            else
            {
                throw new InvalidOperationException("TestUnexpectedResult");
            }
        }, Microsoft.Extensions.Options.Options.Create(target.Options), Microsoft.Extensions.Options.Options.Create(target.Local), target.Installation);
        RuntimeWriteGate writes = new();
        writes.Open(target.Installation.Current!.RuntimeGeneration);
        await boundary.InvokeAsync(context, new AcceptedPeerTrustSource(fixture.State, fixture.First.Time), target.Readiness,
            fixture.First.Time, target.Runtime, target.View, target.Tls, writes);
        fixture.AfterSend?.Invoke(target, path);
        if (fixture.LoseResponse == path)
        {
            fixture.LoseResponse = null;
            throw new HttpRequestException("TestPeerResponseLost");
        }

        byte[] bytes = ((MemoryStream)context.Response.Body).ToArray();
        if (fixture.EditResponse is not null)
        {
            bytes = fixture.EditResponse(path, bytes);
        }

        HttpResponseMessage response = new((HttpStatusCode)context.Response.StatusCode) { Content = new ByteArrayContent(bytes) };
        if (context.Response.ContentType is not null)
        {
            response.Content.Headers.ContentType = new(context.Response.ContentType);
        }

        if (context.Response.Headers.TryGetValue(PeerHeaders.ENVELOPE, out Microsoft.Extensions.Primitives.StringValues envelope))
        {
            response.Headers.Add(PeerHeaders.ENVELOPE, envelope.ToString());
        }

        return response;
    }

    private class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
