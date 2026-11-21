using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly bool pooled;
    private readonly ManagedPeerTransport.ConfirmConnection? confirm;
    private bool connected;

    internal EnrollmentRoutingHandler(EnrollmentRoutingFixture fixture, EnrollmentRoutingNode source, bool pooled,
        ManagedPeerTransport.ConfirmConnection confirm) : this(fixture, source)
    {
        this.pooled = pooled;
        this.confirm = confirm;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath;
        EnrollmentRoutingNode target = fixture.Nodes.Single(node => new Uri(node.Options.PublicUri).Authority == request.RequestUri.Authority);
        bool handoff = path is EnrollmentPeerProtocol.HANDOFF or EnrollmentPeerProtocol.RETIREMENT_HANDOFF;
        bool renew = path == EnrollmentPeerProtocol.RENEW || handoff;
        bool rotation = EnrollmentPeerProtocol.IsRotationAdmission(path);
        AcceptedPeerTrustSource trust = new(fixture.State, fixture.First.Time);
        X509Certificate2 clientLeaf = fixture.UsePublishedPeerLeaves ? source.Tls.Certificate! : source.Certificate;
        X509Certificate2 serverLeaf = fixture.UsePublishedPeerLeaves ? target.Tls.Certificate! : target.Certificate;
        X509Certificate2 ingressLeaf = clientLeaf;
        if (renew || rotation)
        {
            fixture.BeforeRenewalBinding?.Invoke();
            clientLeaf = fixture.RenewalClientLeaf ?? clientLeaf;
            serverLeaf = fixture.RenewalServerLeaf ?? serverLeaf;
            ingressLeaf = fixture.RenewalIngressLeaf ?? ingressLeaf;
        }

        if (!(renew || rotation) || !fixture.SkipRenewalConnectionBinding)
        {
            if (confirm is null)
            {
                await PeerClientHandler.BindServerAsync(request, serverLeaf, trust, fixture.First.Time, token, clientLeaf);
            }
            else if (!pooled || !connected)
            {
                await confirm(request, serverLeaf, clientLeaf, token);
                connected = true;
                fixture.ManagedHandshakes++;
            }
        }

        if (fixture.BeforeReadBody is { } beforeReadBody)
        {
            await beforeReadBody(request.Content!, token);
        }

        byte[] body = await request.Content!.ReadAsByteArrayAsync(token);
        body = fixture.EditBody?.Invoke(path, body) ?? body;
        fixture.Sent.Add((source.Options.NodeId, target.Options.NodeId, path, body));
        fixture.BeforeSend?.Invoke(target, path);
        if (fixture.OfflineSigner && path is EnrollmentPeerProtocol.ISSUE or EnrollmentPeerProtocol.GRANT or EnrollmentPeerProtocol.RENEW)
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
        string requestEnvelope = request.Headers.GetValues(PeerHeaders.ENVELOPE).Single();
        if ((path == EnrollmentPeerProtocol.RENEW || rotation) && fixture.EditRenewalRequest is not null)
        {
            requestEnvelope = PeerHeaders.Encode(fixture.EditRenewalRequest(PeerHeaders.Decode(requestEnvelope)!));
        }

        context.Request.Headers[PeerHeaders.ENVELOPE] = requestEnvelope;
        context.Connection.LocalPort = target.Options.PeerListenPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
        context.Connection.ClientCertificate = ingressLeaf;
        if (renew || rotation)
        {
            var snapshot = target.Tls.Capture();
            context.Features.Set(handoff || rotation || fixture.UsePublishedPeerLeaves ? snapshot
                : fixture.RenewalConnectionSnapshot ?? (snapshot?.CredentialRevision == 0 ? snapshot : null));
        }

        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit());
        context.Response.Body = new MemoryStream();
        context.RequestAborted = token;
        PeerBoundaryMiddleware boundary = new(async current =>
        {
            using IServiceScope? scope = fixture.ControllerServices.GetValueOrDefault(target.Options.NodeId)?.CreateScope();
            EnrollmentPeerController controller = scope is null
                ? new(target.Service, target.Limits, target.Log, fixture.RenewalEnabled ? target.Renewal : null,
                    fixture.RotationAdmissionEnabled ? target.RotationAdmission : null)
                : ActivatorUtilities.CreateInstance<EnrollmentPeerController>(scope.ServiceProvider);
            controller.ControllerContext = new() { HttpContext = current };
            IActionResult result;
            if (path == EnrollmentPeerProtocol.COORDINATE)
            {
                result = await controller.CoordinateAsync(token);
            }
            else if (path == EnrollmentPeerProtocol.ISSUE)
            {
                result = await controller.IssueAsync(token);
            }
            else if (path == EnrollmentPeerProtocol.RENEW)
            {
                result = await controller.RenewAsync(token);
            }
            else if (path == EnrollmentPeerProtocol.HANDOFF)
            {
                result = await controller.HandoffAsync(token);
            }
            else if (path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF)
            {
                result = await controller.RetirementHandoffAsync(token);
            }
            else if (rotation)
            {
                result = path switch
                {
                    EnrollmentPeerProtocol.ROTATION_NONCE => await controller.RotationNonceAsync(token),
                    EnrollmentPeerProtocol.ROTATION_AUTHORIZE => await controller.RotationAuthorizeAsync(token),
                    EnrollmentPeerProtocol.ROTATION_BIND => await controller.RotationBindAsync(token),
                    EnrollmentPeerProtocol.ROTATION_RETIRE => await controller.RotationRetireAsync(token),
                    _ => throw new InvalidOperationException("TestUnexpectedRoute")
                };
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
        await boundary.InvokeAsync(context, trust, target.Readiness,
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
            string reply = envelope.ToString();
            if ((path == EnrollmentPeerProtocol.RENEW || rotation) && fixture.EditRenewalReply is not null)
            {
                reply = PeerHeaders.Encode(fixture.EditRenewalReply(PeerHeaders.Decode(reply)!));
            }

            response.Headers.Add(PeerHeaders.ENVELOPE, reply);
        }

        return response;
    }

    private class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
